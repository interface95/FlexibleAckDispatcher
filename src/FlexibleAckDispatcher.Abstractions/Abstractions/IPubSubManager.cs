using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FlexibleAckDispatcher.Abstractions;

namespace FlexibleAckDispatcher.Abstractions;

/// <summary>
/// 发布订阅管理器，对外提供订阅、发布能力。
/// </summary>
public interface IPubSubManager : IAsyncDisposable
{
    /// <summary>
    /// 直接发布一条消息。
    /// </summary>
    ValueTask PublishAsync<T>(T message, CancellationToken cancellationToken = default);

    /// <summary>
    /// 注册一个新的订阅者（委托方式）。
    /// </summary>
    ValueTask<IPubSubSubscription> SubscribeAsync<T>(WorkerProcessingDelegate<T> handler, CancellationToken cancellationToken = default);

    /// <summary>
    /// 注册一个新的订阅者（接口方式）。
    /// </summary>
    ValueTask<IPubSubSubscription> SubscribeAsync<T>(IWorkerMessageHandler<T> handler, CancellationToken cancellationToken = default);

    /// <summary>
    /// 注册一个新的订阅者（委托 + 配置）。
    /// </summary>
    ValueTask<IPubSubSubscription> SubscribeAsync<T>(WorkerProcessingDelegate<T> handler, Action<SubscriptionOptions>? configure, CancellationToken cancellationToken = default);

    /// <summary>
    /// 注册一个新的订阅者（接口 + 配置）。
    /// </summary>
    ValueTask<IPubSubSubscription> SubscribeAsync<T>(IWorkerMessageHandler<T> handler, Action<SubscriptionOptions>? configure, CancellationToken cancellationToken = default);

    /// <summary>
    /// 手动确认消息处理完成。
    /// </summary>
    ValueTask AckAsync(long deliveryTag, CancellationToken cancellationToken = default);

    /// <summary>
    /// 当前订阅数量。
    /// </summary>
    int SubscriberCount { get; }

    /// <summary>
    /// 当前空闲 Worker 数量。
    /// </summary>
    int IdleCount { get; }

    /// <summary>
    /// 当前执行中的任务数量。
    /// </summary>
    int RunningCount { get; }

    /// <summary>
    /// 未被消费的消息数量。
    /// </summary>
    long PendingCount { get; }

    /// <summary>
    /// 已推送任务总数。
    /// </summary>
    long DispatchedCount { get; }

    /// <summary>
    /// 已完成任务总数。
    /// </summary>
    long CompletedCount { get; }

    /// <summary>
    /// 获取当前所有 Worker 端点的快照。
    /// </summary>
    IReadOnlyList<WorkerEndpointSnapshot> GetSnapshot();
}


