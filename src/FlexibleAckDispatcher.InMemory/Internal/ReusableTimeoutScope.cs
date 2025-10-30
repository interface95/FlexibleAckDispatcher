using System.Collections.Concurrent;

namespace FlexibleAckDispatcher.InMemory.Internal;

/// <summary>
/// 可复用的超时包装器：为 handler 创建带超时的取消令牌，处理完毕后归还对象池。
/// </summary>
internal sealed class ReusableTimeoutScope : IDisposable
{
    private const int MaxPoolSize = 100;

    private static readonly ConcurrentQueue<ReusableTimeoutScope> Pool = new();
    private static int _poolCount;

    private CancellationTokenSource _cts = new();
    private CancellationTokenSource? _linkedCts;
    private bool _inUse;
    private bool _pooled;

    private ReusableTimeoutScope()
    {
    }

    /// <summary>
    /// 从对象池租借一个 Scope，并创建带指定超时的链接令牌。
    /// </summary>
    public static ReusableTimeoutScope Rent(CancellationToken outerToken, TimeSpan timeout, out CancellationToken linkedToken)
    {
        if (!Pool.TryDequeue(out var scope))
        {
            if (Interlocked.Increment(ref _poolCount) <= MaxPoolSize)
            {
                scope = new ReusableTimeoutScope { _pooled = true };
            }
            else
            {
                Interlocked.Decrement(ref _poolCount);
                scope = new ReusableTimeoutScope { _pooled = false };
            }
        }
        else
        {
            scope._pooled = true;
        }

        scope._inUse = true;
        scope._cts.CancelAfter(timeout);
        scope._linkedCts = CancellationTokenSource.CreateLinkedTokenSource(outerToken, scope._cts.Token);
        linkedToken = scope._linkedCts.Token;
        return scope;
    }

    /// <summary>
    /// 归还 Scope，重置内部状态并放回对象池。
    /// </summary>
    public void Dispose()
    {
        if (!_inUse)
        {
            return;
        }

        _cts.CancelAfter(Timeout.InfiniteTimeSpan);
        _linkedCts?.Dispose();
        _linkedCts = null;

        if (!_cts.TryReset())
        {
            _cts.Dispose();
            _cts = new CancellationTokenSource();
        }

        _inUse = false;

        if (_pooled)
        {
            Pool.Enqueue(this);
        }
        else
        {
            _cts.Dispose();
        }
    }
}
