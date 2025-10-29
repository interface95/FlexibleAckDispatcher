using System.Threading.Channels;
using InMemoryWorkerBalancer.Abstractions;

namespace InMemoryWorkerBalancer.Internal;

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
