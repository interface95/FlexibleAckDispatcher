using FlexibleAckDispatcher.Abstractions;
using Microsoft.Extensions.Logging;

namespace FlexibleAckDispatcher.InMemory.Core.Internal;

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
        ILogger logger,
        TimeSpan? ackMonitorInterval,
        IWorkerSelectionStrategy selectionStrategy)
    {
        return new WorkerManager(
            globalToken,
            logger,
            ackMonitorInterval,
            selectionStrategy);
    }
}


