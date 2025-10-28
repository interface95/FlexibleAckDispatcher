using System;
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
    private static readonly TimeSpan MinimumAckMonitorInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan MaximumAckMonitorInterval = TimeSpan.FromSeconds(1);

    private readonly object _lock = new();
    private readonly object _ackMonitorLock = new();
    private readonly List<WorkerEndpoint> _workers = new();
    private readonly Dictionary<int, WorkerEndpoint> _workerMap = new();
    private readonly ConcurrentDictionary<long, WorkerAckToken> _inFlight = new();
    private readonly ConcurrentDictionary<long, AckTimeoutEntry> _ackTimeouts = new();
    private readonly PriorityQueue<WorkerEndpoint, int> _availableWorkers = new();
    private readonly SemaphoreSlim _availableWorkerSignal = new(0);
    private readonly CancellationToken _globalToken;
    private readonly ILogger _logger;
    private readonly TimeSpan? _configuredAckMonitorInterval;
    private CancellationTokenSource? _ackMonitorCts;
    private Task? _ackMonitorTask;
    private TimeSpan? _effectiveAckMonitorInterval;
    private int _ackMonitorStopped;
    private int _workerIdSeed;
    private int _availableWorkerCount;
    private int _disposed;
    private long _dispatchedCount;
    private long _completedCount;
    private long _pendingCount;

    private readonly record struct AckTimeoutEntry(int WorkerId, DateTimeOffset Deadline, TimeSpan Timeout);

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
    internal int IdleCount => Math.Max(Volatile.Read(ref _availableWorkerCount), 0);

    /// <summary>
    /// 当前执行中的任务数量（全局 In-Flight）。
    /// </summary>
    internal int RunningCount => _inFlight.Count;

    /// <summary>
    /// 调度队列中的 Worker 数量。
    /// </summary>
    internal int WorkerQueueCount
    {
        get
        {
            lock (_lock)
            {
                return _availableWorkers.Count;
            }
        }
    }

    internal long DispatchedCount => Interlocked.Read(ref _dispatchedCount);

    internal long CompletedCount => Interlocked.Read(ref _completedCount);

    internal long PendingCount => Math.Max(Interlocked.Read(ref _pendingCount), 0);

    internal WorkerManager(CancellationToken globalToken, ILogger logger, TimeSpan? ackMonitorInterval)
    {
        _globalToken = globalToken;
        _logger = logger;
        _configuredAckMonitorInterval = ackMonitorInterval;
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
    internal async ValueTask<WorkerEndpoint> AddWorkerAsync(WorkerProcessingDelegate handler, SubscriptionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var endpoint = CreateWorkerEndpoint(options);

        var processor = new WorkerProcessor(endpoint, endpoint.Channel.Reader, handler, this);
        processor.Start();

        await RaiseWorkerAddedAsync(endpoint).ConfigureAwait(false);

        EnqueueAvailableWorker(endpoint);
        _logger.LogDebug("Worker {WorkerId} added", endpoint.Id);
        return endpoint;
    }

    private WorkerEndpoint CreateWorkerEndpoint(SubscriptionOptions options)
    {
        var workerId = Interlocked.Increment(ref _workerIdSeed);
        var channel = Channel.CreateBounded<ReadOnlyMemory<byte>>(new BoundedChannelOptions(options.Prefetch)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false
        });

        var capacity = new WorkerCapacity(options.ConcurrencyLimit);
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_globalToken);
        var endpoint = new WorkerEndpoint(workerId, channel, linkedCts, capacity)
        {
            Name = options.Name,
            HandlerTimeout = options.HandlerTimeout,
            FailureThreshold = options.FailureThreshold,
            AckTimeout = options.AckTimeout
        };

        lock (_lock)
        {
            _workers.Add(endpoint);
            _workerMap[workerId] = endpoint;
        }

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
        Interlocked.Increment(ref _dispatchedCount);

        TryReturnIfCapacityAvailable(endpoint);
        return token;
    }

    /// <summary>
    /// 根据 deliveryTag 尝试确认一条消息。
    /// </summary>
    internal bool TryAck(long deliveryTag)
    {
        if (!_inFlight.TryRemove(deliveryTag, out var token))
            return false;

        var result = token.TryAck();
        if (!result)
        {
            _logger.LogWarning("Ack attempt ignored for deliveryTag {DeliveryTag}", deliveryTag);
        }

        RemoveAckTimeout(deliveryTag);

        if (result)
        {
            Interlocked.Increment(ref _completedCount);
        }

        return result;
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
    internal void ForceRelease(long deliveryTag, string? reason = null)
    {
        if (!_inFlight.TryRemove(deliveryTag, out var token)) 
            return;
        RemoveAckTimeout(deliveryTag);
        token.ForceRelease();
        if (string.IsNullOrEmpty(reason))
        {
            _logger.LogWarning("Force released deliveryTag {DeliveryTag}", deliveryTag);
        }
        else
        {
            _logger.LogWarning("Force released deliveryTag {DeliveryTag} ({Reason})", deliveryTag, reason);
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
        endpoint.Capacity.Dispose();

        await RaiseWorkerRemovedAsync(endpoint).ConfigureAwait(false);
        _logger.LogDebug("Worker {WorkerId} removed", endpoint.Id);
        return true;
    }

    /// <summary>
    /// 尝试从可用队列中租用一个 Worker。
    /// </summary>
    internal bool TryRentAvailableWorker(out WorkerEndpoint endpoint)
    {
        endpoint = null!;

        if (!_availableWorkerSignal.Wait(0))
        {
            return false;
        }

        lock (_lock)
        {
            while (_availableWorkers.TryDequeue(out var candidate, out _))
            {
                Interlocked.Decrement(ref _availableWorkerCount);

                if (!_workerMap.TryGetValue(candidate.Id, out var current) || !ReferenceEquals(current, candidate))
                {
                    continue;
                }

                if (!current.IsActive)
                {
                    continue;
                }

                if (current.Capacity.CurrentConnections >= current.Capacity.MaxConnections)
                {
                    continue;
                }

                endpoint = current;
                return true;
            }
        }

        _availableWorkerSignal.Release();
        return false;
    }

    /// <summary>
    /// 等待直到有 Worker 可用。
    /// </summary>
    internal async Task WaitForWorkerAvailableAsync(CancellationToken cancellationToken)
    {
        await _availableWorkerSignal.WaitAsync(cancellationToken).ConfigureAwait(false);
        _availableWorkerSignal.Release();
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="endpoint"></param>
    private void ReleaseWorker(WorkerEndpoint endpoint)
    {
        if (!endpoint.IsActive) 
            return;
        
        EnqueueAvailableWorker(endpoint);
        _logger.LogTrace("Worker {WorkerId} returned to available pool", endpoint.Id);
    }

    private void TryReturnIfCapacityAvailable(WorkerEndpoint endpoint)
    {
        if (!endpoint.IsActive)
            return;

        if (endpoint.Capacity.CurrentConnections >= endpoint.Capacity.MaxConnections)
            return;
        
        EnqueueAvailableWorker(endpoint);
        _logger.LogTrace("Worker {WorkerId} still has capacity, re-queued for dispatch", endpoint.Id);
    }

    private void EnqueueAvailableWorker(WorkerEndpoint endpoint)
    {
        if (!endpoint.IsActive)
        {
            return;
        }

        var current = endpoint.Capacity.CurrentConnections;
        if (current >= endpoint.Capacity.MaxConnections)
        {
            return;
        }

        lock (_lock)
        {
            _availableWorkers.Enqueue(endpoint, current);
        }

        Interlocked.Increment(ref _availableWorkerCount);
        _availableWorkerSignal.Release();
    }

    internal void RegisterAckTimeout(WorkerEndpoint endpoint, WorkerAckToken token)
    {
        if (endpoint.AckTimeout is null || token.IsAcknowledged)
            return;

        var timeout = endpoint.AckTimeout.Value;
        EnsureAckMonitorStarted(timeout);

        if (_ackMonitorTask is null)
        {
            // 未配置 Ack 监控时，仅记录日志提醒。
            _logger.LogDebug("Ack timeout monitoring disabled; deliveryTag {DeliveryTag} will not auto-release.", token.DeliveryTag);
            return;
        }

        var deadline = DateTimeOffset.UtcNow + timeout;
        _ackTimeouts[token.DeliveryTag] = new AckTimeoutEntry(endpoint.Id, deadline, timeout);
    }

    private void EnsureAckMonitorStarted(TimeSpan ackTimeout)
    {
        if (_ackMonitorTask is not null)
            return;

        lock (_ackMonitorLock)
        {
            if (_ackMonitorTask is not null)
                return;

            var interval = _configuredAckMonitorInterval ?? CalculateAckMonitorInterval(ackTimeout);
            _effectiveAckMonitorInterval = interval;
            _ackMonitorCts = new CancellationTokenSource();
            Volatile.Write(ref _ackMonitorStopped, 0);
            
            _ackMonitorTask = Task.Factory.StartNew(
                () => MonitorAckTimeoutsAsync(_ackMonitorCts!, interval),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default).Unwrap();
        }
    }

    private static TimeSpan CalculateAckMonitorInterval(TimeSpan ackTimeout)
    {
        var milliseconds = ackTimeout.TotalMilliseconds / 10d;
        if (double.IsNaN(milliseconds) || double.IsInfinity(milliseconds) || milliseconds <= 0)
        {
            milliseconds = MinimumAckMonitorInterval.TotalMilliseconds;
        }

        milliseconds = Math.Clamp(milliseconds, MinimumAckMonitorInterval.TotalMilliseconds, MaximumAckMonitorInterval.TotalMilliseconds);
        return TimeSpan.FromMilliseconds(milliseconds);
    }

    private void RemoveAckTimeout(long deliveryTag)
    {
        _ackTimeouts.TryRemove(deliveryTag, out _);
    }

    private async Task MonitorAckTimeoutsAsync(CancellationTokenSource cts, TimeSpan interval)
    {
        using var timer = new PeriodicTimer(interval);

        try
        {
            while (await timer.WaitForNextTickAsync(cts.Token).ConfigureAwait(false))
            {
                if (_ackTimeouts.IsEmpty)
                {
                    continue;
                }

                var now = DateTimeOffset.UtcNow;
                foreach (var kvp in _ackTimeouts)
                {
                    if (kvp.Value.Deadline <= now && _ackTimeouts.TryRemove(kvp.Key, out var entry))
                    {
                        ForceRelease(kvp.Key, $"ack timeout after {entry.Timeout}");
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ShutdownAckTimeoutMonitorAsync()
    {
        if (_ackMonitorTask is null || _ackMonitorCts is null)
        {
            return;
        }

        if (Interlocked.Exchange(ref _ackMonitorStopped, 1) != 0)
        {
            return;
        }

        try
        {
            _ackMonitorCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            await _ackMonitorTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _ackMonitorCts.Dispose();
            _ackTimeouts.Clear();
            _ackMonitorCts = null;
            _ackMonitorTask = null;
            _effectiveAckMonitorInterval = null;
            Volatile.Write(ref _ackMonitorStopped, 0);
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
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

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
            endpoint.Capacity.Dispose();
        }

        Interlocked.Exchange(ref _availableWorkerCount, 0);
        await ShutdownAckTimeoutMonitorAsync().ConfigureAwait(false);
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
                _logger.LogError(ex, "Exception occurred in WorkerAdded event handler for Worker {WorkerId}",
                    endpoint.Id);
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
                _logger.LogError(ex, "Exception occurred in WorkerRemoved event handler for Worker {WorkerId}",
                    endpoint.Id);
            }
        }
    }

    internal async ValueTask ReturnPayloadAsync(WorkerEndpoint endpoint, ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
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

    internal void IncrementPending()
    {
        Interlocked.Increment(ref _pendingCount);
    }

    internal void DecrementPending()
    {
        var value = Interlocked.Decrement(ref _pendingCount);
        if (value < 0)
        {
            Interlocked.Exchange(ref _pendingCount, 0);
        }
    }
}