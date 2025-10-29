using System.Collections.Generic;

namespace InMemoryWorkerBalancer.Remote;

/// <summary>
/// 描述远程 Worker 的基本能力与元数据。
/// </summary>
public sealed record RemoteWorkerDescriptor
{
    /// <summary>
    /// Worker 友好名称，便于日志追踪。
    /// </summary>
    public required string WorkerName { get; init; }

    /// <summary>
    /// 预取数量，与 <see cref="SubscriptionOptions"/> 中的概念保持一致。
    /// </summary>
    public int Prefetch { get; init; } = SubscriptionOptions.DefaultPrefetch;

    /// <summary>
    /// Worker 内部允许的最大并发度。
    /// </summary>
    public int ConcurrencyLimit { get; init; } = SubscriptionOptions.DefaultPrefetch;

    /// <summary>
    /// 附加元数据，可用于携带子进程信息或环境标签。
    /// </summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
        = new Dictionary<string, string>();
}

