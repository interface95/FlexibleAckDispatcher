namespace FlexibleAckDispatcher.Abstractions;

/// <summary>
/// 生产者用于发布消息的通道接口。
/// </summary>
public interface IPubSubChannel<in T>
{
    /// <summary>
    /// 将消息发布到调度队列。
    /// </summary>
    ValueTask PublishAsync(T message, CancellationToken cancellationToken = default);
}


