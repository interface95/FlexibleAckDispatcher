namespace InMemoryWorkerBalancer.Abstractions;

/// <summary>
/// 发布订阅管理器，对外提供订阅、发布能力。
/// </summary>
public interface IPubSubManager<T> : IAsyncDisposable
{
    IPubSubChannel<T> Channel { get; }

    /// <summary>
    /// 注册一个新的订阅者（委托方式）。
    /// </summary>
    ValueTask<IPubSubSubscription> SubscribeAsync(WorkerProcessingDelegate<T> handler, CancellationToken cancellationToken = default);

    /// <summary>
    /// 注册一个新的订阅者（接口方式）。
    /// </summary>
    ValueTask<IPubSubSubscription> SubscribeAsync(IWorkerMessageHandler<T> handler, CancellationToken cancellationToken = default);

    /// <summary>
    /// 注册一个新的订阅者（委托 + 配置）。
    /// </summary>
    ValueTask<IPubSubSubscription> SubscribeAsync(WorkerProcessingDelegate<T> handler, Action<SubscriptionOptions>? configure, CancellationToken cancellationToken = default);

    /// <summary>
    /// 注册一个新的订阅者（接口 + 配置）。
    /// </summary>
    ValueTask<IPubSubSubscription> SubscribeAsync(IWorkerMessageHandler<T> handler, Action<SubscriptionOptions>? configure, CancellationToken cancellationToken = default);

    /// <summary>
    /// 手动确定消息处理完成。
    /// </summary>
    /// <param name="deliveryTag"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    ValueTask AckAsync(long deliveryTag, CancellationToken cancellationToken = default);

    int SubscriberCount { get; }
}


