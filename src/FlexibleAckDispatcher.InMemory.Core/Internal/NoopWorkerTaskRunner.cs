using System;
using System.Threading;
using System.Threading.Tasks;

namespace FlexibleAckDispatcher.InMemory.Core.Internal;

internal sealed class NoopWorkerTaskRunner : IWorkerTaskRunner
{
    public static NoopWorkerTaskRunner Instance { get; } = new();

    private NoopWorkerTaskRunner()
    {
    }

    public bool IsStopped => false;

    public int FailureCount => 0;

    public TimeSpan CurrentTaskDuration => TimeSpan.Zero;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
