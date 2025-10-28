using System.Threading;

namespace WorkerBalancer;

/// <summary>
/// 代表一条正在处理的消息，用于手动确认和容量释放。
/// </summary>
public sealed class WorkerAckToken<T>
{
    private readonly Action _release;
    private int _state; // 0: pending, 1: acked, 2: released

    /// <summary>
    /// 构造一个新的 Ack token。
    /// </summary>
    public WorkerAckToken(int workerId, long deliveryTag, T payload, Action release)
    {
        WorkerId = workerId;
        DeliveryTag = deliveryTag;
        Payload = payload;
        _release = release;
    }

    /// <summary>
    /// 所属 Worker Id。
    /// </summary>
    public int WorkerId { get; }

    /// <summary>
    /// 全局唯一的 deliveryTag。
    /// </summary>
    public long DeliveryTag { get; }

    /// <summary>
    /// 消息负载。
    /// </summary>
    public T Payload { get; }

    /// <summary>
    /// 当前是否已经确认。
    /// </summary>
    public bool IsAcknowledged => Volatile.Read(ref _state) == 1;

    /// <summary>
    /// 尝试确认该消息。
    /// </summary>
    public bool TryAck()
    {
        if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
        {
            return false;
        }

        _release();
        return true;
    }

    /// <summary>
    /// 强制释放该消息（在异常或取消时使用）。
    /// </summary>
    public void ForceRelease()
    {
        if (Interlocked.Exchange(ref _state, 2) == 0)
        {
            _release();
        }
    }
}

