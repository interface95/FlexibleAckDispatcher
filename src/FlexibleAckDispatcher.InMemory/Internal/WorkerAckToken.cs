namespace FlexibleAckDispatcher.InMemory.Internal;

/// <summary>
/// 代表一条正在处理的消息，用于手动确认和容量释放。
/// </summary>
internal sealed class WorkerAckToken
{
    private readonly Action _release;
    private int _state;

    private enum TokenState
    {
        Pending = 0,
        Acknowledged = 1,
        Released = 2
    }

    /// <summary>
    /// 构造一个新的 Ack token。
    /// </summary>
    public WorkerAckToken(int workerId, long deliveryTag, ReadOnlyMemory<byte> payload, Action release)
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
    public ReadOnlyMemory<byte> Payload { get; }

    /// <summary>
    /// 当前是否已经确认。
    /// </summary>
    public bool IsAcknowledged => (TokenState)Volatile.Read(ref _state) == TokenState.Acknowledged;

    /// <summary>
    /// 尝试确认该消息。
    /// </summary>
    public bool TryAck()
    {
        if (Interlocked.CompareExchange(ref _state, (int)TokenState.Acknowledged, (int)TokenState.Pending) != (int)TokenState.Pending)
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
        if (Interlocked.Exchange(ref _state, (int)TokenState.Released) == (int)TokenState.Pending)
        {
            _release();
        }
    }
}

