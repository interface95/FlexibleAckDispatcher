using System;
using System.Threading.Tasks;
using InMemoryWorkerBalancer.Internal;

namespace InMemoryWorkerBalancer;

/// <summary>
/// 传递给订阅者的消息包装器，携带手动确认所需的信息。
/// </summary>
public readonly struct WorkerMessage<T>
{
    private readonly WorkerDeliveryContext _context;

    internal WorkerMessage(WorkerDeliveryContext context, T payload)
    {
        _context = context;
        Payload = payload;
    }

    /// <summary>
    /// 执行该消息的 Worker Id。
    /// </summary>
    public int WorkerId => _context.WorkerId;

    /// <summary>
    /// 消息的全局唯一 deliveryTag。
    /// </summary>
    public long DeliveryTag => _context.DeliveryTag;

    /// <summary>
    /// 消息处理启动时间（UTC）。
    /// </summary>
    public DateTimeOffset StartedAt => _context.StartedAt;

    /// <summary>
    /// 消息负载。
    /// </summary>
    public T Payload { get; }

    /// <summary>
    /// 手动确认该消息。
    /// </summary>
    public ValueTask AckAsync() => _context.AckAsync();

    internal WorkerDeliveryContext Context => _context;
}

