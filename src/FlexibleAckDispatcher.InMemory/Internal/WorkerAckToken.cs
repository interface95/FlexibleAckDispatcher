namespace FlexibleAckDispatcher.InMemory.Internal;

/// <summary>
/// 代表一条正在处理的消息，用于手动确认和容量释放。
/// </summary>
internal sealed class WorkerAckToken
{
    private readonly Action _processingRelease;
    private readonly Action _ackRelease;
    private int _state;
    private int _processingReleased;
    private int _ackReleased;

    private enum TokenState
    {
        Pending = 0,
        Acknowledged = 1,
        Released = 2
    }

    /// <summary>
    /// 构造一个新的 Ack token。
    /// </summary>
    public WorkerAckToken(
        int workerId,
        long deliveryTag,
        ReadOnlyMemory<byte> payload,
        Action processingRelease,
        Action ackRelease)
    {
        WorkerId = workerId;
        DeliveryTag = deliveryTag;
        Payload = payload;
        _processingRelease = processingRelease;
        _ackRelease = ackRelease;
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
        var result = Interlocked.CompareExchange(ref _state, (int)TokenState.Acknowledged, (int)TokenState.Pending) ==
                     (int)TokenState.Pending;

        if (result)
        {
            ReleaseAckSlot();
        }

        return result;
    }

    /// <summary>
    /// 在处理完成后释放并发槽位。
    /// </summary>
    public void ReleaseProcessingSlot()
    {
        if (Interlocked.Exchange(ref _processingReleased, 1) == 0)
        {
            _processingRelease();
        }
    }

    /// <summary>
    /// 强制释放该消息（在异常或取消时使用）。
    /// </summary>
    public void ForceRelease()
    {
        var previous = Interlocked.Exchange(ref _state, (int)TokenState.Released);
        if (previous == (int)TokenState.Pending)
        {
            ReleaseAckSlot();
        }
    }

    private void ReleaseAckSlot()
    {
        if (Interlocked.Exchange(ref _ackReleased, 1) == 0)
        {
            _ackRelease();
        }
    }
}

