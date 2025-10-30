using System.Threading;
using System.Threading.Channels;
using FlexibleAckDispatcher.Abstractions.Remote;
using FlexibleAckDispatcher.InMemory.Core.Internal;
using Microsoft.Extensions.Logging;

namespace FlexibleAckDispatcher.InMemory.Remote;

internal sealed class GrpcWorkerTaskRunnerFactory : IWorkerTaskRunnerFactory
{
    private readonly IRemoteWorkerBridge _bridge;
    private readonly RemoteWorkerContext _context;
    private readonly ILogger _logger;
    private int _created;

    public GrpcWorkerTaskRunnerFactory(IRemoteWorkerBridge bridge, RemoteWorkerContext context, ILogger logger)
    {
        _bridge = bridge;
        _context = context;
        _logger = logger;
    }

    public IWorkerTaskRunner Create(WorkerEndpoint endpoint, ChannelReader<ReadOnlyMemory<byte>> reader, WorkerProcessingDelegate handler, WorkerManager workerManager)
    {
        if (Interlocked.CompareExchange(ref _created, 1, 0) == 0)
        {
            _context.Endpoint = endpoint;
            return new GrpcWorkerTaskRunner(endpoint, reader, workerManager, _bridge, _context, _logger);
        }

        return NoopWorkerTaskRunner.Instance;
    }
}
