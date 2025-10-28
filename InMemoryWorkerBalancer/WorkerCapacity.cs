using System.Threading;

namespace WorkerBalancer;

/// <summary>
/// Worker 的容量控制器，限制同一时间正在处理的消息数量。
/// </summary>
public sealed class WorkerCapacity
{
    private readonly SemaphoreSlim _semaphore;
    private int _current;

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
        if (_semaphore.Wait(0))
        {
            Interlocked.Increment(ref _current);
            return true;
        }

        return false;
    }

    /// <summary>
    /// 等待直到占用一个并发槽位。
    /// </summary>
    public async ValueTask WaitAsync(CancellationToken cancellationToken)
    {
        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        Interlocked.Increment(ref _current);
    }

    /// <summary>
    /// 释放一个并发槽位。
    /// </summary>
    public void Release()
    {
        var current = Interlocked.Decrement(ref _current);
        if (current < 0)
        {
            Interlocked.Exchange(ref _current, 0);
            throw new InvalidOperationException("Capacity released more times than it was acquired.");
        }

        _semaphore.Release();
    }
}


