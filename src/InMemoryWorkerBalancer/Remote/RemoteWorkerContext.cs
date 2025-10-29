using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using InMemoryWorkerBalancer.Internal;

namespace InMemoryWorkerBalancer.Remote;

internal sealed class RemoteWorkerContext
{
    public RemoteWorkerContext(RemoteWorkerSession session)
    {
        Session = session;
        InFlight = new ConcurrentDictionary<long, RemoteInFlightState>();

        var messageType = "application/octet-stream";
        IReadOnlyDictionary<string, string>? headers = null;

        if (session.Descriptor.Metadata is { Count: > 0 })
        {
            headers = session.Descriptor.Metadata;
            if (session.Descriptor.Metadata.TryGetValue("message_type", out var typeName) && !string.IsNullOrWhiteSpace(typeName))
            {
                messageType = typeName;
            }
        }

        MessageType = messageType;
        Headers = headers;
    }

    public RemoteWorkerSession Session { get; }

    public WorkerEndpoint Endpoint { get; set; } = null!;

    public ConcurrentDictionary<long, RemoteInFlightState> InFlight { get; }

    public string MessageType { get; }

    public IReadOnlyDictionary<string, string>? Headers { get; }
}

internal sealed class RemoteInFlightState
{
    public RemoteInFlightState(long deliveryTag, ReadOnlyMemory<byte> payload, WorkerEndpoint endpoint, TaskCompletionSource<bool> completion)
    {
        DeliveryTag = deliveryTag;
        Payload = payload;
        Endpoint = endpoint;
        Completion = completion;
    }

    public long DeliveryTag { get; }

    public ReadOnlyMemory<byte> Payload { get; }

    public WorkerEndpoint Endpoint { get; }

    public TaskCompletionSource<bool> Completion { get; }
}