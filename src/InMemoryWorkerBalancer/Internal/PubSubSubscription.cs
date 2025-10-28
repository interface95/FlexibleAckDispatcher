using System.Threading;
using System.Threading.Tasks;
using InMemoryWorkerBalancer.Abstractions;

namespace InMemoryWorkerBalancer.Internal;

/// <summary>
/// 订阅句柄，实现释放逻辑。
/// </summary>
internal sealed class PubSubSubscription : IPubSubSubscription
{
    private readonly PubSubManager _owner;
    private int _disposed;

    public PubSubSubscription(PubSubManager owner, int id)
    {
        _owner = owner;
        Id = id;
    }

    public int Id { get; }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _owner.RemoveSubscriptionAsync(Id).ConfigureAwait(false);
    }
}

