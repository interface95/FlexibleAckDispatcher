using System.Threading;

namespace InMemoryWorkerBalancer.Internal;

/// <summary>
/// 轮询选择策略：按顺序依次选择可用的 Worker。
/// </summary>
public sealed class RoundRobinSelectionStrategy : IWorkerSelectionStrategy
{
    private readonly object _lock = new();
    private readonly Dictionary<int, WorkerState> _workerMap = new();
    private readonly SemaphoreSlim _availableWorkerSignal = new(0);
    private int _currentIndex;
    private int _disposed;

    private readonly record struct WorkerState(int CurrentConcurrency, int MaxConcurrency, bool IsActive);

    /// <summary>
    /// 获取当前可立即处理消息的 Worker 数量。
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
    /// 获取内部等待队列的近似长度（按可用 Worker 统计）。
    /// </summary>
    public int QueueLength
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
    /// 更新指定 Worker 的最新快照信息。
    /// </summary>
    public void Update(WorkerEndpointSnapshot snapshot)
    {
        lock (_lock)
        {
            var newState = new WorkerState(snapshot.CurrentConcurrency, snapshot.MaxConcurrency, snapshot.IsActive);
            _workerMap[snapshot.Id] = newState;

            // 检查是否有任何Worker可用
            var hasAvailable = _workerMap.Values.Any(w => w.IsActive && w.CurrentConcurrency < w.MaxConcurrency);

            if (!hasAvailable) 
                return;
            
            // 确保信号量有计数（可能已经有了，没关系）
            try
            {
                _availableWorkerSignal.Release();
            }
            catch (SemaphoreFullException)
            {
                // 信号量已经有计数了，忽略
            }
        }
    }

    /// <summary>
    /// 尝试租用一个可用的 Worker（轮询方式）。
    /// </summary>
    public bool TryRent(out int workerId)
    {
        workerId = -1;

        // 非阻塞检查信号量
        if (!_availableWorkerSignal.Wait(0))
            return false;
        
        lock (_lock)
        {
            if (_workerMap.Count == 0)
            {
                _availableWorkerSignal.Release(); // 还回去
                return false;
            }

            // 获取所有WorkerId并排序，确保稳定的轮询顺序
            var workerIds = _workerMap.Keys.OrderBy(id => id).ToArray();
            if (workerIds.Length == 0)
            {
                _availableWorkerSignal.Release();
                return false;
            }

            var startIndex = _currentIndex % workerIds.Length;
            
            // 从当前索引开始轮询查找可用Worker
            for (var i = 0; i < workerIds.Length; i++)
            {
                var index = (startIndex + i) % workerIds.Length;
                var candidateId = workerIds[index];

                if (!_workerMap.TryGetValue(candidateId, out var state))
                {
                    continue;
                }

                if (!state.IsActive || state.CurrentConcurrency >= state.MaxConcurrency)
                {
                    continue;
                }

                // 找到可用Worker
                workerId = candidateId;
                _currentIndex = index + 1; // 下次从下一个位置开始
                
                // 检查是否还有其他可用Worker
                var stillHasAvailable = _workerMap.Values.Any(w => 
                    w.IsActive && w.CurrentConcurrency < w.MaxConcurrency);
                
                if (stillHasAvailable)
                {
                    _availableWorkerSignal.Release(); // 保持信号量有效
                }
                
                return true;
            }

            // 没找到可用Worker，还回信号量
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
            // 检查是否还有Worker可用
            var hasAvailable = _workerMap.Values.Any(w => w.IsActive && w.CurrentConcurrency < w.MaxConcurrency);
            if (hasAvailable)
            {
                _availableWorkerSignal.Release(); // 保持信号
            }
        }
    }

    /// <summary>
    /// 移除指定的 Worker。
    /// </summary>
    public void Remove(int workerId)
    {
        lock (_lock)
        {
            _workerMap.Remove(workerId);
        }
    }

    /// <summary>
    /// 释放内部持有的资源。
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) == 0)
        {
            _availableWorkerSignal.Dispose();
        }
    }
}
