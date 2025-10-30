using System.Threading.Channels;
using FlexibleAckDispatcher.Abstractions;

namespace FlexibleAckDispatcher.InMemory.Core.Internal;

internal sealed class DefaultWorkerTaskRunnerFactory : IWorkerTaskRunnerFactory
{
    public static DefaultWorkerTaskRunnerFactory Instance { get; } = new();

    private DefaultWorkerTaskRunnerFactory()
    {
    }

    public IWorkerTaskRunner Create(WorkerEndpoint endpoint, ChannelReader<ReadOnlyMemory<byte>> reader, WorkerProcessingDelegate handler, WorkerManager workerManager)
    {
        return new WorkerTaskRunner(endpoint, reader, handler, workerManager);
    }
}
