using System;
using System.Threading.Tasks;

namespace InMemoryWorkerBalancer.Internal;

/// <summary>
/// Internal representation of a payload delivery used by the worker runtime.
/// </summary>
internal readonly struct WorkerDeliveryContext
{
    private readonly WorkerAckToken _token;
    private readonly Func<long, bool> _ackCallback;

    public WorkerDeliveryContext(WorkerAckToken token, Func<long, bool> ackCallback, DateTimeOffset startedAt)
    {
        _token = token;
        _ackCallback = ackCallback;
        StartedAt = startedAt;
    }

    public int WorkerId => _token.WorkerId;

    public long DeliveryTag => _token.DeliveryTag;

    public ReadOnlyMemory<byte> Payload => _token.Payload;

    public DateTimeOffset StartedAt { get; }

    public ValueTask AckAsync()
    {
        if (_ackCallback(DeliveryTag))
        {
            return ValueTask.CompletedTask;
        }

        throw new InvalidOperationException("Message has already been acknowledged or released.");
    }

    public WorkerAckToken Token => _token;
}

