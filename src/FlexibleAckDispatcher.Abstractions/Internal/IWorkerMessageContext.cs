using System;
using System.Threading.Tasks;

namespace FlexibleAckDispatcher.Abstractions.Internal;

/// <summary>
/// 抽象消息上下文，供内部 Worker 运行时使用。
/// </summary>
internal interface IWorkerMessageContext
{
    /// <summary>
    /// 获取当前消息所属的 Worker 标识。
    /// </summary>
    int WorkerId { get; }

    /// <summary>
    /// 获取消息的 Delivery Tag。
    /// </summary>
    long DeliveryTag { get; }

    /// <summary>
    /// 获取任务开始处理的时间戳（UTC）。
    /// </summary>
    DateTimeOffset StartedAt { get; }

    /// <summary>
    /// 手动确认消息。
    /// </summary>
    ValueTask AckAsync();
}
