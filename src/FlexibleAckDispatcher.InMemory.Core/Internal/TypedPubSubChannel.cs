using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using FlexibleAckDispatcher.Abstractions;

namespace FlexibleAckDispatcher.InMemory.Core.Internal;

/// <summary>
/// 类型化的发布通道实现，用于序列化消息并写入共享队列。
/// </summary>
internal sealed class TypedPubSubChannel<T> : IPubSubChannel<T>
{
    private readonly ChannelWriter<ReadOnlyMemory<byte>> _writer;
    private readonly IWorkerPayloadSerializer _serializer;
    private readonly WorkerManager _workerManager;

    public TypedPubSubChannel(ChannelWriter<ReadOnlyMemory<byte>> writer, IWorkerPayloadSerializer serializer, WorkerManager workerManager)
    {
        _writer = writer;
        _serializer = serializer;
        _workerManager = workerManager;
    }

    public async ValueTask PublishAsync(T message, CancellationToken cancellationToken = default)
    {
        var payload = _serializer.Serialize(message);
        _workerManager.IncrementPending();

        try
        {
            await _writer.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _workerManager.DecrementPending();
            throw;
        }
    }
}

