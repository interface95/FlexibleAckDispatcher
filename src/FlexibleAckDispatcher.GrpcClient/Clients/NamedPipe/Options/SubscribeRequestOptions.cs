using System;
using InMemoryWorkerBalancer.Remote.Protos;

namespace FlexibleAckDispatcher.GrpcClient.Clients.NamedPipe.Options;

/// <summary>
/// 订阅流请求配置。
/// </summary>
public sealed class SubscribeRequestOptions
{
    /// <summary>
    /// Worker 唯一标识。
    /// </summary>
    public int WorkerId { get; private set; }

    /// <summary>
    /// 设置订阅的 WorkerId。
    /// </summary>
    public SubscribeRequestOptions WithWorkerId(int workerId)
    {
        if (workerId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(workerId), "WorkerId 必须大于 0。");
        }

        WorkerId = workerId;
        return this;
    }

    internal SubscribeRequest ToRequest() => new() { WorkerId = WorkerId };

    internal void Validate()
    {
        if (WorkerId <= 0)
        {
            throw new InvalidOperationException("WorkerId 必须大于 0。");
        }
    }
}

