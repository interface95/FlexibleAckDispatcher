using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FlexibleAckDispatcher.Abstractions;

namespace FlexibleAckDispatcher.InMemory.Internal;

/// <summary>
/// 基于优先队列的 Worker 选择策略：优先选择当前并发数最少的 Worker。
/// 使用 PriorityQueue 数据结构实现高效的最少连接数调度。
/// </summary>
public sealed class PriorityQueueWorkerSelectionStrategy : IWorkerSelectionStrategy
{
    private readonly object _lock = new();
    private readonly PriorityQueue<QueueEntry, int> _availableWorkers = new();
    private readonly Dictionary<int, WorkerState> _workerMap = new();
    private readonly HashSet<int> _queuedWorkers = new(); // 跟踪已在队列中的 Worker
    private readonly SemaphoreSlim _availableWorkerSignal = new(0);
    private long _sequence;
    private int _disposed;

    /// <summary>
    /// 优先队列中的条目，包含 WorkerId、负载和序列号（用于负载相同时按时间排序）。
    /// </summary>
    private readonly record struct QueueEntry(int WorkerId, int Load, long Sequence);

    /// <summary>
    /// Worker 状态信息，用于跟踪 Worker 的并发数和活跃状态。
    /// </summary>
    private readonly record struct WorkerState(int CurrentConcurrency, int MaxConcurrency, bool IsActive);

    /// <summary>
    /// 当前可用的空闲 Worker 数量。
    /// </summary>
    public int IdleCount
    {
        get
        {
            lock (_lock)
            {
                return _workerMap.Values.Count(w => w.IsActive && w.CurrentConcurrency < w.MaxConcurrency);
            }
        }
    }

    /// <summary>
    /// 调度队列中的 Worker 数量。
    /// </summary>
    public int QueueLength
    {
        get
        {
            lock (_lock)
            {
                return _availableWorkers.Count;
            }
        }
    }

    /// <summary>
    /// 更新指定 Worker 的最新快照信息。
    /// </summary>
    public void Update(WorkerEndpointSnapshot snapshot)
    {
        lock (_lock)
        {
            var state = new WorkerState(snapshot.CurrentConcurrency, snapshot.MaxConcurrency, snapshot.IsActive);
            _workerMap[snapshot.Id] = state;

            // 如果 Worker 有可用容量，加入/更新队列
            var hasCapacity = state.IsActive && state.CurrentConcurrency < state.MaxConcurrency;
            
            if (hasCapacity)
            {
                // 总是重新入队以确保优先级是最新的
                // 旧的条目会在 TryRent 时被过滤掉
                EnqueueWorker(snapshot.Id, state.CurrentConcurrency);
                _queuedWorkers.Add(snapshot.Id); // HashSet.Add 是幂等的
            }
            else if (!hasCapacity && _queuedWorkers.Contains(snapshot.Id))
            {
                // Worker 没有容量了，从队列标记中移除（实际条目会在 TryRent 时过滤）
                _queuedWorkers.Remove(snapshot.Id);
            }

            // 确保信号量状态正确
            var anyAvailable = _queuedWorkers.Count > 0;
            if (anyAvailable)
            {
                try
                {
                    _availableWorkerSignal.Release();
                }
                catch (SemaphoreFullException)
                {
                    // 信号量已满，忽略
                }
            }
        }
    }

    /// <summary>
    /// 尝试租用一个可用的 Worker（优先负载最低的）。
    /// </summary>
    public bool TryRent(out int workerId)
    {
        workerId = -1;

        if (!_availableWorkerSignal.Wait(0))
        {
            return false;
        }

        lock (_lock)
        {
            // 从优先队列中找到第一个真正可用的 Worker
            while (_availableWorkers.TryDequeue(out var entry, out _))
            {
                // 从队列标记中移除
                _queuedWorkers.Remove(entry.WorkerId);

                if (!_workerMap.TryGetValue(entry.WorkerId, out var state))
                {
                    continue;
                }

                if (!state.IsActive || state.CurrentConcurrency >= state.MaxConcurrency)
                {
                    continue;
                }

                // 找到可用 Worker
                workerId = entry.WorkerId;
                
                // 如果还有其他可用 Worker，保持信号量
                if (_queuedWorkers.Count > 0)
                {
                    _availableWorkerSignal.Release();
                }
                
                return true;
            }

            // 队列空了，清空标记
            _queuedWorkers.Clear();
            
            // 没找到可用 Worker，还回信号量
            _availableWorkerSignal.Release();
            return false;
        }
    }

    /// <summary>
    /// 等待直到有 Worker 可用。
    /// </summary>
    public async Task WaitForWorkerAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        await _availableWorkerSignal.WaitAsync(cancellationToken).ConfigureAwait(false);
        
        lock (_lock)
        {
            // 检查是否还有 Worker 可用
            if (_queuedWorkers.Count > 0)
            {
                _availableWorkerSignal.Release(); // 保持信号
            }
        }
    }

    /// <summary>
    /// Worker 被移除时的清理逻辑。
    /// </summary>
    public void Remove(int workerId)
    {
        lock (_lock)
        {
            _workerMap.Remove(workerId);
            _queuedWorkers.Remove(workerId);
            // 注意：优先队列中可能还残留该 Worker 的 ID，
            // 但在 TryRent 时会通过 _workerMap 检查过滤掉
        }
    }

    /// <summary>
    /// 将 Worker 加入可用队列（内部方法，需在锁内调用）。
    /// 注意：可能会产生重复条目（相同WorkerId的旧条目），但会在TryRent时被过滤。
    /// </summary>
    private void EnqueueWorker(int workerId, int currentLoad)
    {
        // 使用当前并发数作为优先级（并发数越少优先级越高）
        // 序列号用于在负载相同时按时间顺序排序（FIFO）
        var sequence = Interlocked.Increment(ref _sequence);
        var entry = new QueueEntry(workerId, currentLoad, sequence);
        _availableWorkers.Enqueue(entry, currentLoad);
        // 注意：不在这里管理信号量，由调用者在Update中统一处理
    }

    /// <summary>
    /// 释放资源。
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) == 0)
        {
            _availableWorkerSignal.Dispose();
        }
    }
}
