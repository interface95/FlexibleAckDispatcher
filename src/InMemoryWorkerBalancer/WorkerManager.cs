using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using InMemoryWorkerBalancer.Internal;
using Microsoft.Extensions.Logging;

namespace InMemoryWorkerBalancer;

/// <summary>
/// 管理 Worker 生命周期与消息调度的核心类。
/// </summary>
public sealed class WorkerManager<T>
{
    private readonly object _lock = new();
    private readonly List<WorkerEndpoint<T>> _workers = new();
    private readonly Dictionary<int, WorkerEndpoint<T>> _workerMap = new();
    private readonly ConcurrentDictionary<long, WorkerAckToken<T>> _inFlight = new();
    private readonly Channel<WorkerEndpoint<T>> _availableWorkers;
    private readonly CancellationToken _globalToken;
    private readonly ILogger _logger;
    private int _workerIdSeed;

    internal ILogger Logger => _logger;

    /// <summary>
    /// Worker 添加事件（异步）。
    /// </summary>
    public event Func<WorkerEndpoint<T>, Task>? WorkerAdded;

    /// <summary>
    /// Worker 移除事件（异步）。
    /// </summary>
    public event Func<WorkerEndpoint<T>, Task>? WorkerRemoved;

    public WorkerManager(CancellationToken globalToken, ILogger logger)
    {
        _globalToken = globalToken;
        _logger = logger;

        _availableWorkers = Channel.CreateUnbounded<WorkerEndpoint<T>>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false
        });
    }

    /// <summary>
    /// 注册一个新的 Worker。
    /// </summary>
    public WorkerEndpoint<T> AddWorker(WorkerProcessingDelegate<T> handler, SubscriptionOptions options)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        WorkerEndpoint<T> endpoint;

        lock (_lock)
        {
            var workerId = ++_workerIdSeed;
            var prefetch = options.Prefetch;
            var concurrencyLimit = options.ConcurrencyLimit;

            var channel = Channel.CreateBounded<T>(new BoundedChannelOptions(prefetch)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = false,
                SingleWriter = false
            });

            var capacity = new WorkerCapacity(concurrencyLimit);
            var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_globalToken);
            endpoint = new WorkerEndpoint<T>(workerId, channel, linkedCts, capacity)
            {
                Name = options.Name,
                HandlerTimeout = options.HandlerTimeout,
                FailureThreshold = options.FailureThreshold
            };
            _workers.Add(endpoint);
            _workerMap[workerId] = endpoint;
        }

        var processor = new WorkerProcessor<T>(endpoint, endpoint.Channel.Reader, handler, this);
        processor.Start();
        
        // 异步触发事件，不阻塞当前调用
        _ = Task.Run(async () =>
        {
            try
            {
                await RaiseWorkerAddedAsync(endpoint).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error while raising WorkerAdded event for Worker {WorkerId}", endpoint.Id);
            }
        });
        
        _availableWorkers.Writer.TryWrite(endpoint);
        _logger.LogInformation("Worker {WorkerId} added", endpoint.Id);
        return endpoint;
    }

    internal async ValueTask<WorkerAckToken<T>> RegisterInFlightAsync(
        WorkerEndpoint<T> endpoint,
        T payload,
        CancellationToken cancellationToken)
    {
        await endpoint.Capacity.WaitAsync(cancellationToken).ConfigureAwait(false);

        var deliveryTag = SnowflakeIdGenerator.NextId();
        var token = new WorkerAckToken<T>(endpoint.Id, deliveryTag, payload, () =>
        {
            endpoint.Capacity.Release();
            ReleaseWorker(endpoint);
            _logger.LogDebug("Worker {WorkerId} released slot for deliveryTag {DeliveryTag}", endpoint.Id, deliveryTag);
        });
        _inFlight[deliveryTag] = token;
        _logger.LogDebug("Worker {WorkerId} acquired slot for deliveryTag {DeliveryTag}", endpoint.Id, deliveryTag);

        TryReturnIfCapacityAvailable(endpoint);
        return token;
    }

    /// <summary>
    /// 根据 deliveryTag 尝试确认一条消息。
    /// </summary>
    internal bool TryAck(long deliveryTag)
    {
        if (_inFlight.TryRemove(deliveryTag, out var token))
        {
            var result = token.TryAck();
            if (!result)
            {
                _logger.LogWarning("Ack attempt ignored for deliveryTag {DeliveryTag}", deliveryTag);
            }
            return result;
        }

        return false;
    }

    /// <summary>
    /// 根据 deliveryTag 查询正在处理的消息。
    /// </summary>
    internal bool TryGetMessage(long deliveryTag, out WorkerAckToken<T> token)
    {
        return _inFlight.TryGetValue(deliveryTag, out token!);
    }

    /// <summary>
    /// 在异常或取消时强制释放未确认的消息。
    /// </summary>
    internal void ForceRelease(long deliveryTag)
    {
        if (_inFlight.TryRemove(deliveryTag, out var token))
        {
            token.ForceRelease();
            _logger.LogWarning("Force released deliveryTag {DeliveryTag}", deliveryTag);
        }
    }

    /// <summary>
    /// 移除并停止指定 Worker。
    /// </summary>
    public async Task<bool> RemoveWorkerAsync(int workerId)
    {
        WorkerEndpoint<T>? endpoint;

        lock (_lock)
        {
            if (!_workerMap.TryGetValue(workerId, out endpoint))
            {
                return false;
            }

            _workers.Remove(endpoint);
            _workerMap.Remove(workerId);
            endpoint.IsActive = false;
        }

        endpoint.Channel.Writer.TryComplete();
        endpoint.Cancellation.Cancel();

        await endpoint.WaitForCompletionAsync().ConfigureAwait(false);

        await RaiseWorkerRemovedAsync(endpoint).ConfigureAwait(false);
        _logger.LogInformation("Worker {WorkerId} removed", endpoint.Id);
        return true;
    }

    /// <summary>
    /// 获取当前所有 Worker 的快照。
    /// </summary>
    public WorkerEndpoint<T>[] GetSnapshot()
    {
        lock (_lock)
        {
            return _workers.ToArray();
        }
    }

    /// <summary>
    /// 尝试从可用队列中租用一个 Worker。
    /// </summary>
    public bool TryRentAvailableWorker(out WorkerEndpoint<T> endpoint)
    {
        while (_availableWorkers.Reader.TryRead(out endpoint))
        {
            if (!endpoint.IsActive)
            {
                continue;
            }

            return true;
        }

        endpoint = null!;
        return false;
    }

    /// <summary>
    /// 等待直到有 Worker 可用。
    /// </summary>
    public async Task WaitForWorkerAvailableAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (await _availableWorkers.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return;
            }
        }
    }

    public void ReleaseWorker(WorkerEndpoint<T> endpoint)
    {
        if (endpoint.IsActive)
        {
            _availableWorkers.Writer.TryWrite(endpoint);
            _logger.LogTrace("Worker {WorkerId} returned to available pool", endpoint.Id);
        }
    }

    private void TryReturnIfCapacityAvailable(WorkerEndpoint<T> endpoint)
    {
        if (!endpoint.IsActive)
        {
            return;
        }

        if (endpoint.Capacity.CurrentConnections < endpoint.Capacity.MaxConnections)
        {
            _availableWorkers.Writer.TryWrite(endpoint);
            _logger.LogTrace("Worker {WorkerId} still has capacity, re-queued for dispatch", endpoint.Id);
        }
    }

    /// <summary>
    /// 完成所有 Worker 的 Writer，停止后续消息写入。
    /// </summary>
    public void CompleteWriters()
    {
        WorkerEndpoint<T>[] snapshot;
        lock (_lock)
        {
            snapshot = _workers.ToArray();
        }

        foreach (var endpoint in snapshot)
        {
            endpoint.Channel.Writer.TryComplete();
        }
    }

    /// <summary>
    /// 停止所有 Worker 并等待其任务完成。
    /// </summary>
    public async Task StopAllAsync()
    {
        WorkerEndpoint<T>[] snapshot;
        lock (_lock)
        {
            snapshot = _workers.ToArray();
        }

        foreach (var endpoint in snapshot)
        {
            endpoint.Channel.Writer.TryComplete();
            endpoint.Cancellation.Cancel();
        }

        await Task.WhenAll(snapshot.Select(ep => ep.WaitForCompletionAsync())).ConfigureAwait(false);
    }

    /// <summary>
    /// 当前活跃的 Worker 数量。
    /// </summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _workers.Count;
            }
        }
    }

    /// <summary>
    /// 安全触发 WorkerAdded 事件（异步），捕获订阅者抛出的异常。
    /// </summary>
    private async Task RaiseWorkerAddedAsync(WorkerEndpoint<T> endpoint)
    {
        var handlers = WorkerAdded;
        if (handlers == null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                await ((Func<WorkerEndpoint<T>, Task>)handler).Invoke(endpoint).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception occurred in WorkerAdded event handler for Worker {WorkerId}", endpoint.Id);
            }
        }
    }

    /// <summary>
    /// 安全触发 WorkerRemoved 事件（异步），捕获订阅者抛出的异常。
    /// </summary>
    private async Task RaiseWorkerRemovedAsync(WorkerEndpoint<T> endpoint)
    {
        var handlers = WorkerRemoved;
        if (handlers == null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                await ((Func<WorkerEndpoint<T>, Task>)handler).Invoke(endpoint).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception occurred in WorkerRemoved event handler for Worker {WorkerId}", endpoint.Id);
            }
        }
    }
}
