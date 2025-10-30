using System;
using FlexibleAckDispatcher.GrpcServer.Protos;

namespace FlexibleAckDispatcher.GrpcClient.Clients.NamedPipe.Options;

/// <summary>
/// NACK 请求配置。
/// </summary>
public sealed class NackRequestOptions
{
    /// <summary>
    /// 交付标签。
    /// </summary>
    public long DeliveryTag { get; private set; }

    /// <summary>
    /// 是否重新入队。
    /// </summary>
    public bool Requeue { get; private set; }

    /// <summary>
    /// 拒绝原因。
    /// </summary>
    public string? Reason { get; private set; }

    /// <summary>
    /// Worker 唯一标识。
    /// </summary>
    public int WorkerId { get; private set; }

    /// <summary>
    /// 设置交付标签。
    /// </summary>
    public NackRequestOptions WithDeliveryTag(long deliveryTag)
    {
        if (deliveryTag <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(deliveryTag), "DeliveryTag 必须大于 0。");
        }

        DeliveryTag = deliveryTag;
        return this;
    }

    /// <summary>
    /// 设置是否重新入队。
    /// </summary>
    public NackRequestOptions WithRequeue(bool requeue = true)
    {
        Requeue = requeue;
        return this;
    }

    /// <summary>
    /// 设置拒绝原因。
    /// </summary>
    public NackRequestOptions WithReason(string? reason)
    {
        Reason = reason;
        return this;
    }

    /// <summary>
    /// 设置 WorkerId。
    /// </summary>
    public NackRequestOptions WithWorkerId(int workerId)
    {
        if (workerId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(workerId), "WorkerId 必须大于 0。");
        }

        WorkerId = workerId;
        return this;
    }

    internal NackRequest ToRequest()
    {
        var request = new NackRequest
        {
            DeliveryTag = DeliveryTag,
            Requeue = Requeue,
            WorkerId = WorkerId
        };

        if (!string.IsNullOrWhiteSpace(Reason))
        {
            request.Reason = Reason;
        }

        return request;
    }

    internal void Validate()
    {
        if (WorkerId <= 0)
        {
            throw new InvalidOperationException("WorkerId 必须大于 0。");
        }

        if (DeliveryTag <= 0)
        {
            throw new InvalidOperationException("DeliveryTag 必须大于 0。");
        }
    }
}

