using Microsoft.Extensions.Logging;

namespace InMemoryWorkerBalancer;

/// <summary>
/// 工厂方法，用于创建默认的 <see cref="WorkerManager{T}"/> 实例。
/// </summary>
public static class WorkerManagerFactory
{
    /// <summary>
    /// 创建一个 WorkerManager。
    /// </summary>
    public static WorkerManager<T> CreateManager<T>(
        CancellationToken globalToken,
        ILogger? logger = null)
    {
        return new WorkerManager<T>(
            globalToken,
            logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
    }
}


