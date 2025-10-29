using System;
using InMemoryWorkerBalancer.Remote.Protos;

namespace InMemoryWorkerBalancer.Remote.Clients.NamedPipe.Options;

/// <summary>
/// ACK 请求配置。
/// </summary>
public sealed class AckRequestOptions
{
    /// <summary>
    /// 交付标签。
    /// </summary>
    public long DeliveryTag { get; private set; }

    /// <summary>
    /// Worker 唯一标识。
    /// </summary>
    public int WorkerId { get; private set; }

    /// <summary>
    /// 设置要确认的 DeliveryTag。
    /// </summary>
    public AckRequestOptions WithDeliveryTag(long deliveryTag)
    {
        if (deliveryTag <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(deliveryTag), "DeliveryTag 必须大于 0。");
        }

        DeliveryTag = deliveryTag;
        return this;
    }

    /// <summary>
    /// 设置 WorkerId。
    /// </summary>
    public AckRequestOptions WithWorkerId(int workerId)
    {
        if (workerId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(workerId), "WorkerId 必须大于 0。");
        }

        WorkerId = workerId;
        return this;
    }

    internal AckRequest ToRequest() => new()
    {
        DeliveryTag = DeliveryTag,
        WorkerId = WorkerId
    };

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

