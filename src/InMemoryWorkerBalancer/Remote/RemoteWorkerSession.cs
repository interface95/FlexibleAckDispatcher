using System;

namespace InMemoryWorkerBalancer.Remote;

/// <summary>
/// 表示与远程 Worker 建立的一个逻辑会话。
/// </summary>
public sealed class RemoteWorkerSession
{
    /// <summary>
    /// 初始化远程 worker 会话。
    /// </summary>
    /// <param name="workerId">远程 worker 的唯一标识。</param>
    /// <param name="descriptor">远程 worker 能力描述。</param>
    public RemoteWorkerSession(int workerId, RemoteWorkerDescriptor descriptor)
    {
        if (workerId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(workerId), "Worker id must be greater than zero.");
        }

        Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
        WorkerId = workerId;
        LastHeartbeat = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// 远程 Worker 的唯一标识。
    /// </summary>
    public int WorkerId { get; }

    /// <summary>
    /// Worker 的能力描述。
    /// </summary>
    public RemoteWorkerDescriptor Descriptor { get; }

    /// <summary>
    /// 最近一次收到心跳的时间戳。
    /// </summary>
    internal DateTimeOffset LastHeartbeat { get; set; }
}

