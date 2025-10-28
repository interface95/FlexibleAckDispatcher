namespace WorkerBalancer;

/// <summary>
/// 传递给订阅者的消息包装器，携带手动确认所需的信息。
/// </summary>
public readonly struct WorkerMessage<T>
{
    private readonly WorkerAckToken<T> _token;
    private readonly Func<long, bool> _ackCallback;

    /// <summary>
    /// 构造消息包装器。
    /// </summary>
    /// <param name="token">内部追踪的 ACK token。</param>
    /// <param name="ackCallback">执行确认的回调。</param>
    public WorkerMessage(WorkerAckToken<T> token, Func<long, bool> ackCallback)
    {
        _token = token;
        _ackCallback = ackCallback;
    }

    /// <summary>
    /// 执行该消息的 Worker Id。
    /// </summary>
    public int WorkerId => _token.WorkerId;

    /// <summary>
    /// 消息的全局唯一 deliveryTag。
    /// </summary>
    public long DeliveryTag => _token.DeliveryTag;

    /// <summary>
    /// 消息负载。
    /// </summary>
    public T Payload => _token.Payload;

    /// <summary>
    /// 手动确认该消息。
    /// </summary>
    public ValueTask AckAsync()
    {
        if (_ackCallback(DeliveryTag))
        {
            return ValueTask.CompletedTask;
        }

        throw new InvalidOperationException("Message has already been acknowledged or released.");
    }

    internal WorkerAckToken<T> Token => _token;
}

