using System;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Channels;

namespace FlexibleAckDispatcher.InMemory.Internal;

internal interface IWorkerTaskRunner
{
    bool IsStopped { get; }

    int FailureCount { get; }

    TimeSpan CurrentTaskDuration { get; }

    Task StartAsync(CancellationToken cancellationToken);
}

internal interface IWorkerTaskRunnerFactory
{
    IWorkerTaskRunner Create(WorkerEndpoint endpoint, ChannelReader<ReadOnlyMemory<byte>> reader, WorkerProcessingDelegate handler, WorkerManager workerManager);
}
