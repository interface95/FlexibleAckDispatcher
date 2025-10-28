using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using InMemoryWorkerBalancer.Abstractions;

namespace InMemoryWorkerBalancer.Internal;

/// <summary>
/// 类型化的发布通道实现，用于序列化消息并写入共享队列。
/// </summary>
internal sealed class TypedPubSubChannel<T> : IPubSubChannel<T>
{
    private readonly ChannelWriter<ReadOnlyMemory<byte>> _writer;
    private readonly IWorkerPayloadSerializer _serializer;

    public TypedPubSubChannel(ChannelWriter<ReadOnlyMemory<byte>> writer, IWorkerPayloadSerializer serializer)
    {
        _writer = writer;
        _serializer = serializer;
    }

    public ValueTask PublishAsync(T message, CancellationToken cancellationToken = default)
    {
        var payload = _serializer.Serialize(message);
        return _writer.WriteAsync(payload, cancellationToken);
    }
}

