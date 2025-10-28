using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace InMemoryWorkerBalancer.Internal;

/// <summary>
/// 管理 Worker 生命周期与消息调度的核心类。
/// </summary>
internal sealed class WorkerManager
{
    private readonly object _lock = new();
    private readonly List<WorkerEndpoint> _workers = new();
    private readonly Dictionary<int, WorkerEndpoint> _workerMap = new();
    private readonly ConcurrentDictionary<long, WorkerAckToken> _inFlight = new();
    private readonly Channel<WorkerEndpoint> _availableWorkers;
    private readonly CancellationToken _globalToken;
    private readonly ILogger _logger;
    private int _workerIdSeed;
    private int _availableWorkerCount;

    internal ILogger Logger => _logger;

    /// <summary>
    /// Worker 添加事件（异步）。
    /// </summary>
    internal event Func<WorkerEndpointSnapshot, Task>? WorkerAdded;

    /// <summary>
    /// Worker 移除事件（异步）。
    /// </summary>
    internal event Func<WorkerEndpointSnapshot, Task>? WorkerRemoved;
    
   /// <summary>
    /// 当前空闲 Worker 数量。
    /// </summary>
    internal int AvailableWorkerCount => Math.Max(Volatile.Read(ref _availableWorkerCount), 0);

    /// <summary>
    /// 当前执行中的任务数量（全局 In-Flight）。
    /// </summary>
    internal int InFlightCount => _inFlight.Count;

    internal WorkerManager(CancellationToken globalToken, ILogger logger)
    {
        _globalToken = globalToken;
        _logger = logger;

        _availableWorkers = Channel.CreateUnbounded<WorkerEndpoint>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false
        });
    }

    /// <summary>
    /// 获取当前所有 Worker 的快照。
    /// </summary>
    internal IReadOnlyList<WorkerEndpointSnapshot> GetSnapshot()
    {
        lock (_lock)
        {
            var result = new WorkerEndpointSnapshot[_workers.Count];
            for (var i = 0; i < _workers.Count; i++)
            {
                result[i] = _workers[i].CreateSnapshot();
            }

            return result;
        }
    }

    /// <summary>
    /// 注册一个新的 Worker。
    /// </summary>
    internal WorkerEndpoint AddWorker(WorkerProcessingDelegate handler, SubscriptionOptions options)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        WorkerEndpoint endpoint;

        lock (_lock)
        {
            var workerId = ++_workerIdSeed;
            var prefetch = options.Prefetch;
            var concurrencyLimit = options.ConcurrencyLimit;

            var channel = Channel.CreateBounded<ReadOnlyMemory<byte>>(new BoundedChannelOptions(prefetch)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = false,
                SingleWriter = false
            });

            var capacity = new WorkerCapacity(concurrencyLimit);
            var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_globalToken);
            endpoint = new WorkerEndpoint(workerId, channel, linkedCts, capacity)
            {
                Name = options.Name,
                HandlerTimeout = options.HandlerTimeout,
                FailureThreshold = options.FailureThreshold
            };
            _workers.Add(endpoint);
            _workerMap[workerId] = endpoint;
        }

        var processor = new WorkerProcessor(endpoint, endpoint.Channel.Reader, handler, this);
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

        EnqueueAvailableWorker(endpoint);
        _logger.LogInformation("Worker {WorkerId} added", endpoint.Id);
        return endpoint;
    }

    internal async ValueTask<WorkerAckToken> RegisterInFlightAsync(
        WorkerEndpoint endpoint,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        await endpoint.Capacity.WaitAsync(cancellationToken).ConfigureAwait(false);

        var deliveryTag = SnowflakeIdGenerator.NextId();
        var token = new WorkerAckToken(endpoint.Id, deliveryTag, payload, () =>
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
    private bool TryGetMessage(long deliveryTag, out WorkerAckToken token)
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
    internal async Task<bool> RemoveWorkerAsync(int workerId)
    {
        WorkerEndpoint? endpoint;

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
        await endpoint.Cancellation.CancelAsync().ConfigureAwait(false);

        await endpoint.WaitForCompletionAsync().ConfigureAwait(false);
        if (endpoint.Fault is not null)
        {
            _logger.LogError(endpoint.Fault, "Worker {WorkerId} stopped with error", endpoint.Id);
        }
        endpoint.Cancellation.Dispose();

        await RaiseWorkerRemovedAsync(endpoint).ConfigureAwait(false);
        _logger.LogInformation("Worker {WorkerId} removed", endpoint.Id);
        return true;
    }

    /// <summary>
    /// 尝试从可用队列中租用一个 Worker。
    /// </summary>
    internal bool TryRentAvailableWorker(out WorkerEndpoint endpoint)
    {
        while (_availableWorkers.Reader.TryRead(out endpoint))
        {
            var remaining = Interlocked.Decrement(ref _availableWorkerCount);
            if (remaining < 0)
            {
                Interlocked.Exchange(ref _availableWorkerCount, 0);
            }
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
    internal async Task WaitForWorkerAvailableAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await _availableWorkers.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return;
            }
        }
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="endpoint"></param>
    private void ReleaseWorker(WorkerEndpoint endpoint)
    {
        if (endpoint.IsActive)
        {
            EnqueueAvailableWorker(endpoint);
            _logger.LogTrace("Worker {WorkerId} returned to available pool", endpoint.Id);
        }
    }

    private void TryReturnIfCapacityAvailable(WorkerEndpoint endpoint)
    {
        if (!endpoint.IsActive)
        {
            return;
        }

        if (endpoint.Capacity.CurrentConnections < endpoint.Capacity.MaxConnections)
        {
            EnqueueAvailableWorker(endpoint);
            _logger.LogTrace("Worker {WorkerId} still has capacity, re-queued for dispatch", endpoint.Id);
        }
    }

    private void EnqueueAvailableWorker(WorkerEndpoint endpoint)
    {
        if (_availableWorkers.Writer.TryWrite(endpoint))
        {
            Interlocked.Increment(ref _availableWorkerCount);
        }
    }

    /// <summary>
    /// 完成所有 Worker 的 Writer，停止后续消息写入。
    /// </summary>
    internal void CompleteWriters()
    {
        WorkerEndpoint[] snapshot;
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
    internal async Task StopAllAsync()
    {
        WorkerEndpoint[] snapshot;
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

        foreach (var endpoint in snapshot)
        {
            endpoint.Cancellation.Dispose();
        }

        Interlocked.Exchange(ref _availableWorkerCount, 0);
    }

    /// <summary>
    /// 当前活跃的 Worker 数量。
    /// </summary>
    private int Count
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
    private async Task RaiseWorkerAddedAsync(WorkerEndpoint endpoint)
    {
        var handlers = WorkerAdded;
        if (handlers == null)
        {
            return;
        }

        var info = endpoint.CreateSnapshot();
        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                await ((Func<WorkerEndpointSnapshot, Task>)handler).Invoke(info).ConfigureAwait(false);
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
    private async Task RaiseWorkerRemovedAsync(WorkerEndpoint endpoint)
    {
        var handlers = WorkerRemoved;
        if (handlers == null)
        {
            return;
        }

        var info = endpoint.CreateSnapshot();
        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                await ((Func<WorkerEndpointSnapshot, Task>)handler).Invoke(info).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception occurred in WorkerRemoved event handler for Worker {WorkerId}", endpoint.Id);
            }
        }
    }

    internal async ValueTask ReturnPayloadAsync(WorkerEndpoint endpoint, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        if (endpoint.IsActive)
        {
            await endpoint.Writer.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            _logger.LogWarning("Discarded payload because worker {WorkerId} is inactive", endpoint.Id);
        }
    }
}
