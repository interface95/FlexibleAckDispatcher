using System;

namespace FlexibleAckDispatcher.GrpcServer.NamedPipe;

/// <summary>
/// 配置基于 Named Pipe + gRPC 的远程 Worker 桥接器。
/// </summary>
public sealed class NamedPipeRemoteWorkerOptions
{
    /// <summary>
    /// 命名管道名称，必须在所有进程间保持一致。
    /// </summary>
    public string PipeName { get; set; } = "FlexibleAckDispatcher";

    /// <summary>
    /// gRPC 服务端并发限制。
    /// </summary>
    public int MaxConcurrentSessions { get; set; } = Environment.ProcessorCount;

    /// <summary>
    /// 子进程与主进程之间的心跳超时时间。
    /// </summary>
    public TimeSpan HeartbeatTimeout { get; set; } = TimeSpan.FromSeconds(30);
}

