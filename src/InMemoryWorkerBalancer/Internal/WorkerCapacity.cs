namespace InMemoryWorkerBalancer.Internal;

/// <summary>
/// Worker 的容量控制器，限制同一时间正在处理的消息数量。
/// </summary>
internal sealed class WorkerCapacity : IDisposable
{
    private readonly SemaphoreSlim _semaphore;
    private int _current;
    private int _disposed;

    /// <summary>
    /// 初始化容量控制器。
    /// </summary>
    /// <param name="maxConnections">允许的最大并发连接数。</param>
    public WorkerCapacity(int maxConnections)
    {
        if (maxConnections <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxConnections), "Max connections must be greater than zero.");
        }

        MaxConnections = maxConnections;
        _semaphore = new SemaphoreSlim(maxConnections, maxConnections);
    }

    /// <summary>
    /// 最大并发连接数。
    /// </summary>
    public int MaxConnections { get; }

    /// <summary>
    /// 当前已占用的连接数。
    /// </summary>
    public int CurrentConnections => Volatile.Read(ref _current);

    /// <summary>
    /// 尝试占用一个并发槽位。
    /// </summary>
    /// <returns>成功占用返回 true，达到上限返回 false。</returns>
    public bool TryAcquire()
    {
        ThrowIfDisposed();

        if (!_semaphore.Wait(0)) 
            return false;
        
        Interlocked.Increment(ref _current);
        return true;

    }

    /// <summary>
    /// 等待直到占用一个并发槽位。
    /// </summary>
    public async ValueTask WaitAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        Interlocked.Increment(ref _current);
    }

    /// <summary>
    /// 释放一个并发槽位。
    /// </summary>
    public void Release()
    {
        ThrowIfDisposed();

        var current = Interlocked.Decrement(ref _current);
        if (current < 0)
        {
            Interlocked.Exchange(ref _current, 0);
            throw new InvalidOperationException("Capacity released more times than it was acquired.");
        }

        _semaphore.Release();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _semaphore.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(WorkerCapacity));
        }
    }
}


