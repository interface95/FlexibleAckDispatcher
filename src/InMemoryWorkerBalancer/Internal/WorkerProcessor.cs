using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace InMemoryWorkerBalancer.Internal;

/// <summary>
/// Worker 的处理器：从专属通道读取消息并调用处理委托。
/// </summary>
internal sealed class WorkerProcessor
{
    private readonly WorkerEndpoint _endpoint;
    private readonly ChannelReader<ReadOnlyMemory<byte>> _reader;
    private readonly WorkerProcessingDelegate _handler;
    private readonly WorkerManager _workerManager;

    public WorkerProcessor(
        WorkerEndpoint endpoint,
        ChannelReader<ReadOnlyMemory<byte>> reader,
        WorkerProcessingDelegate handler,
        WorkerManager workerManager)
    {
        _endpoint = endpoint;
        _reader = reader;
        _handler = handler;
        _workerManager = workerManager;
    }

    public void Start()
    {
        var runningTask = Task.Factory.StartNew(
            () => RunWorkerPoolAsync(_endpoint.Cancellation.Token),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();

        _endpoint.SetRunningTask(runningTask);
    }

    private async Task RunWorkerPoolAsync(CancellationToken cancellationToken)
    {
        var concurrency = _endpoint.Capacity.MaxConnections;
        var workers = new Task[concurrency];
        var failureCount = 0;
        var failureLock = new object();
        var stopped = false;

        _workerManager.Logger.LogInformation("Worker {WorkerId} starting with concurrency={Concurrency}", _endpoint.Id, concurrency);

        for (var i = 0; i < concurrency; i++)
        {
            var processingTask = new WorkerProcessingTask(
                _endpoint,
                _reader,
                _handler,
                _workerManager,
                () => stopped,
                () => ReportFailure(ref failureCount, failureLock, ref stopped));

            workers[i] = Task.Factory.StartNew(
                () => processingTask.RunAsync(cancellationToken),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default).Unwrap();
        }

        try
        {
            await Task.WhenAll(workers).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            _workerManager.Logger.LogInformation("Worker {WorkerId} exiting", _endpoint.Id);

            if (stopped)
            {
                throw new InvalidOperationException($"Worker {_endpoint.Id} stopped after exceeding failure threshold {_endpoint.FailureThreshold}.");
            }
        }
    }

    private void ReportFailure(ref int failureCount, object failureLock, ref bool stopped)
    {
        lock (failureLock)
        {
            failureCount++;
            if (failureCount >= _endpoint.FailureThreshold && !stopped)
            {
                stopped = true;
                _workerManager.Logger.LogError(
                    "Worker {WorkerId} reached failure threshold {Threshold} and will stop.",
                    _endpoint.Id,
                    _endpoint.FailureThreshold);
                if (!_endpoint.Cancellation.IsCancellationRequested)
                {
                    try
                    {
                        _endpoint.Cancellation.Cancel();
                    }
                    catch (ObjectDisposedException)
                    {
                        // Cancellation source already disposed during shutdown; ignore.
                    }
                }
            }
        }
    }
}

