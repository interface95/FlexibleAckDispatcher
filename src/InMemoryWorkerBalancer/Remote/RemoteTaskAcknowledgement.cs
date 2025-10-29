namespace InMemoryWorkerBalancer.Remote;

/// <summary>
/// 表示远程 Worker 成功确认了一条任务。
/// </summary>
public readonly record struct RemoteTaskAcknowledgement(int WorkerId, long DeliveryTag);

