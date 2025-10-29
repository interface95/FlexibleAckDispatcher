namespace InMemoryWorkerBalancer.Remote;

/// <summary>
/// 远程 Worker 报文中约定的元数据键名。
/// </summary>
public static class RemoteWorkerMetadataKeys
{
    /// <summary>
    /// 自定义消息类型的键名。
    /// </summary>
    public const string MessageType = "message_type";

    /// <summary>
    /// 处理超时时间（Ticks）的键名。
    /// </summary>
    public const string HandlerTimeoutTicks = "handler_timeout_ticks";

    /// <summary>
    /// 失败阈值的键名。
    /// </summary>
    public const string FailureThreshold = "failure_threshold";
}

