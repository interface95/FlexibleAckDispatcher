using System.Collections.Concurrent;

namespace WorkerBalancer.Internal;

/// <summary>
/// 可复用的超时包装器：为 handler 创建带超时的取消令牌，处理完毕后归还对象池。
/// </summary>
internal sealed class ReusableTimeoutScope : IDisposable
{
    private static readonly ConcurrentQueue<ReusableTimeoutScope> Pool = new();

    private CancellationTokenSource _cts = new();
    private CancellationTokenSource? _linkedCts;
    private bool _inUse;

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
            scope = new ReusableTimeoutScope();
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
        Pool.Enqueue(this);
    }
}
