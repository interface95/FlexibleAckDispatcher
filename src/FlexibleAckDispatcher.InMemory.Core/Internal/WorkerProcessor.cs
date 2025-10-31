using System;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using FlexibleAckDispatcher.Abstractions;
using Microsoft.Extensions.Logging;

namespace FlexibleAckDispatcher.InMemory.Core.Internal;

/// <summary>
/// Worker 的处理器：从专属通道读取消息并调用处理委托。
/// </summary>
internal sealed class WorkerProcessor
{
    private readonly WorkerEndpoint _endpoint;
    private readonly ChannelReader<ReadOnlyMemory<byte>> _reader;
    private readonly WorkerProcessingDelegate _handler;
    private readonly WorkerManager _workerManager;
    private readonly IWorkerTaskRunnerFactory _taskRunnerFactory;

    public WorkerProcessor(
        WorkerEndpoint endpoint,
        ChannelReader<ReadOnlyMemory<byte>> reader,
        WorkerProcessingDelegate handler,
        WorkerManager workerManager,
        IWorkerTaskRunnerFactory? taskRunnerFactory = null)
    {
        _endpoint = endpoint;
        _reader = reader;
        _handler = handler;
        _workerManager = workerManager;
        _taskRunnerFactory = taskRunnerFactory ?? DefaultWorkerTaskRunnerFactory.Instance;
    }

    public void Start()
    {
        // var runningTask = Task.Factory.StartNew(
        //     () => RunWorkerPoolAsync(_endpoint.Cancellation.Token),
        //     CancellationToken.None,
        //     TaskCreationOptions.LongRunning,
        //     TaskScheduler.Default).Unwrap();

        var runningTask = RunWorkerPoolAsync(_endpoint.Cancellation.Token);
        
        _endpoint.SetRunningTask(runningTask);
    }

    private async Task RunWorkerPoolAsync(CancellationToken cancellationToken)
    {
        var concurrency = _endpoint.Capacity.MaxConnections;
        var workers = new Task[concurrency];
        var taskRunners = new IWorkerTaskRunner[concurrency];

        _workerManager.Logger.LogInformation("Worker {WorkerId} starting with concurrency={Concurrency}", _endpoint.Id, concurrency);

        for (var i = 0; i < concurrency; i++)
        {
            var taskRunner = _taskRunnerFactory.Create(_endpoint, _reader, _handler, _workerManager);
            taskRunners[i] = taskRunner;
            workers[i] = taskRunner.StartAsync(cancellationToken);
        }

        try
        {
            await Task.WhenAll(workers).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _workerManager.Logger.LogError(ex, "Unexpected error in worker pool for Worker {WorkerId}", _endpoint.Id);
            throw;
        }
        finally
        {
            _workerManager.Logger.LogInformation("Worker {WorkerId} exiting", _endpoint.Id);

            if (taskRunners.Any(t => t.IsStopped))
            {
                throw new InvalidOperationException($"Worker {_endpoint.Id} stopped after exceeding failure threshold {_endpoint.FailureThreshold}.");
            }
        }
    }
}

