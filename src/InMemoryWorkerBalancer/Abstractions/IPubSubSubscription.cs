namespace InMemoryWorkerBalancer.Abstractions;

/// <summary>
/// 表示一个订阅者句柄，可用于取消订阅。
/// </summary>
public interface IPubSubSubscription : IAsyncDisposable
{
    /// <summary>
    /// 获取订阅的唯一标识。
    /// </summary>
    int Id { get; }
}


