using System;
using System.Threading;
using System.Threading.Tasks;

namespace FlexibleAckDispatcher.Abstractions.Remote;

/// <summary>
/// 抽象远程 Worker 桥接器，负责管理跨进程连接、任务分发以及 ACK/NACK 回传。
/// </summary>
public interface IRemoteWorkerBridge : IAsyncDisposable
{
    /// <summary>
    /// 当远程 Worker 成功建立会话时触发。
    /// </summary>
    event Func<RemoteWorkerSession, ValueTask>? WorkerConnected;

    /// <summary>
    /// 当远程 Worker 会话终止时触发。
    /// </summary>
    event Func<RemoteWorkerSession, ValueTask>? WorkerDisconnected;

    /// <summary>
    /// 当远程 Worker 确认任务时触发。
    /// </summary>
    event Func<RemoteTaskAcknowledgement, ValueTask>? TaskAcknowledged;

    /// <summary>
    /// 当远程 Worker 拒绝任务时触发。
    /// </summary>
    event Func<RemoteTaskRejection, ValueTask>? TaskRejected;

    /// <summary>
    /// 启动桥接器，准备接受远程 Worker 注册。
    /// </summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 停止桥接器，释放所有资源。
    /// </summary>
    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 将任务推送到指定的远程 Worker 会话中。
    /// </summary>
    ValueTask DispatchAsync(RemoteTaskDispatch dispatch, CancellationToken cancellationToken = default);
}

