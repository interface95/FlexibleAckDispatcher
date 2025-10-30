using System;
using FlexibleAckDispatcher.GrpcServer.Protos;

namespace FlexibleAckDispatcher.GrpcClient.Clients.NamedPipe.Options;

/// <summary>
/// 心跳请求配置。
/// </summary>
public sealed class HeartbeatRequestOptions
{
    /// <summary>
    /// Worker 唯一标识。
    /// </summary>
    public int WorkerId { get; private set; }

    /// <summary>
    /// 指定心跳时间戳，默认为当前 UTC。
    /// </summary>
    public long? Ticks { get; private set; }

    /// <summary>
    /// 设置 WorkerId。
    /// </summary>
    public HeartbeatRequestOptions WithWorkerId(int workerId)
    {
        if (workerId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(workerId), "WorkerId 必须大于 0。");
        }

        WorkerId = workerId;
        return this;
    }

    /// <summary>
    /// 指定心跳时间戳。
    /// </summary>
    public HeartbeatRequestOptions WithTicks(long ticks)
    {
        if (ticks <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ticks), "Ticks 必须大于 0。");
        }

        Ticks = ticks;
        return this;
    }

    internal HeartbeatRequest ToRequest() => new()
    {
        WorkerId = WorkerId,
        Ticks = Ticks ?? DateTimeOffset.UtcNow.Ticks
    };

    internal void Validate()
    {
        if (WorkerId <= 0)
        {
            throw new InvalidOperationException("WorkerId 必须大于 0。");
        }
    }
}

