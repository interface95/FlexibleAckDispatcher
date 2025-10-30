namespace FlexibleAckDispatcher.Abstractions.Remote;

/// <summary>
/// 表示远程 Worker 拒绝或处理失败的任务信息。
/// </summary>
public readonly record struct RemoteTaskRejection(int WorkerId, long DeliveryTag, bool Requeue, string? Reason);

