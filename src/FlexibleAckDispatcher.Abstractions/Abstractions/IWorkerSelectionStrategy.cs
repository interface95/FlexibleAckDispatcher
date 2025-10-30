using System.Threading;
using System.Threading.Tasks;

namespace FlexibleAckDispatcher.Abstractions;

/// <summary>
/// 抽象 Worker 选择策略，允许自定义调度实现。
/// </summary>
public interface IWorkerSelectionStrategy : IDisposable
{
    /// <summary>
    /// 当前可用的空闲 Worker 数量。
    /// </summary>
    int IdleCount { get; }

    /// <summary>
    /// 调度队列中的 Worker 数量。
    /// </summary>
    int QueueLength { get; }

    /// <summary>
    /// 更新指定 Worker 的最新快照信息。
    /// </summary>
    void Update(WorkerEndpointSnapshot snapshot);

    /// <summary>
    /// 尝试租用一个可用的 Worker。
    /// </summary>
    bool TryRent(out int workerId);

    /// <summary>
    /// 等待直到有 Worker 可用。
    /// </summary>
    Task WaitForWorkerAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Worker 被移除时的清理逻辑。
    /// </summary>
    void Remove(int workerId);
}