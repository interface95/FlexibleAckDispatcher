namespace WorkerBalancer.Abstractions;

/// <summary>
/// 表示一个订阅者句柄，可用于取消订阅。
/// </summary>
public interface IPubSubSubscription : IAsyncDisposable
{
    int Id { get; }
}


