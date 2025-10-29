using System.Threading.Channels;
using InMemoryWorkerBalancer.Internal;

namespace InMemoryWorkerBalancer.Abstractions;

/// <summary>
/// 表示 Worker 在执行期间可替换的任务运行器。
/// </summary>
public interface IWorkerTaskRunner
{
    /// <summary>
    /// 获取运行器是否已停止。
    /// </summary>
    bool IsStopped { get; }

    /// <summary>
    /// 获取累计失败次数。
    /// </summary>
    int FailureCount { get; }

    /// <summary>
    /// 获取当前任务的执行时长。
    /// </summary>
    TimeSpan CurrentTaskDuration { get; }

    /// <summary>
    /// 启动运行器并进入任务处理循环。
    /// </summary>
    /// <param name="cancellationToken">允许外部取消逻辑。</param>
    Task StartAsync(CancellationToken cancellationToken);
}

/// <summary>
/// 创建 <see cref="IWorkerTaskRunner"/> 实例的工厂。
/// </summary>
internal interface IWorkerTaskRunnerFactory
{
    /// <summary>
    /// 创建新的任务运行器实例。
    /// </summary>
    /// <param name="endpoint">关联的 worker 端点。</param>
    /// <param name="reader">任务读取通道。</param>
    /// <param name="handler">业务处理委托。</param>
    /// <param name="workerManager">worker 管理器。</param>
    IWorkerTaskRunner Create(WorkerEndpoint endpoint, ChannelReader<ReadOnlyMemory<byte>> reader, WorkerProcessingDelegate handler, WorkerManager workerManager);
}
