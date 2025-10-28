using Microsoft.Extensions.Logging;

namespace InMemoryWorkerBalancer.Internal;

/// <summary>
/// 工厂方法，用于创建默认的 <see cref="WorkerManager"/> 实例。
/// </summary>
internal static class WorkerManagerFactory
{
    /// <summary>
    /// 创建一个 WorkerManager。
    /// </summary>
    internal static WorkerManager CreateManager(
        CancellationToken globalToken,
        ILogger? logger = null,
        TimeSpan? ackMonitorInterval = null)
    {
        return new WorkerManager(
            globalToken,
            logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            ackMonitorInterval);
    }
}


