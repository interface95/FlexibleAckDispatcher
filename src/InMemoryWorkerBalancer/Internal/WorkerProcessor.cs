using System.Collections.Generic;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace InMemoryWorkerBalancer.Internal;

/// <summary>
/// Worker 的处理器：从专属通道读取消息并调用处理委托。
/// </summary>
internal sealed class WorkerProcessor<T>
{
    private readonly WorkerEndpoint<T> _endpoint;
    private readonly ChannelReader<T> _reader;
    private readonly WorkerProcessingDelegate<T> _handler;
    private readonly WorkerManager<T> _workerManager;

    public WorkerProcessor(
        WorkerEndpoint<T> endpoint,
        ChannelReader<T> reader,
        WorkerProcessingDelegate<T> handler,
        WorkerManager<T> workerManager)
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
            workers[i] = Task.Factory.StartNew(
                () => WorkerLoopAsync(() => stopped, () => ReportFailure(ref failureCount, failureLock, ref stopped), cancellationToken),
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

    private async Task WorkerLoopAsync(Func<bool> isStopped, Action reportFailure, CancellationToken cancellationToken)
    {
        try
        {
            while (!isStopped() && await _reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (!isStopped() && _reader.TryRead(out var payload))
                {
                    WorkerAckToken<T>? token = null;
                    try
                    {
                        token = await _workerManager.RegisterInFlightAsync(_endpoint, payload, cancellationToken).ConfigureAwait(false);
                        _workerManager.Logger.LogDebug("Worker {WorkerId} handling deliveryTag {DeliveryTag}", _endpoint.Id, token.DeliveryTag);

                        await ExecuteWithTimeoutAsync(token, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        reportFailure();
                        if (token is not null)
                        {
                            _workerManager.ForceRelease(token.DeliveryTag);
                        }
                        else if (!cancellationToken.IsCancellationRequested)
                        {
                            try
                            {
                                await _workerManager.ReturnPayloadAsync(_endpoint, payload, cancellationToken).ConfigureAwait(false);
                            }
                            catch (ChannelClosedException)
                            {
                                _workerManager.Logger.LogWarning("Worker {WorkerId} channel closed while returning payload", _endpoint.Id);
                            }
                        }
                        _workerManager.Logger.LogError(ex, "Worker {WorkerId} handling failed", _endpoint.Id);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task ExecuteWithTimeoutAsync(WorkerAckToken<T> token, CancellationToken outerCancellationToken)
    {
        using var scope = ReusableTimeoutScope.Rent(outerCancellationToken, _endpoint.HandlerTimeout, out var linkedToken);

        try
        {
            await _handler(new WorkerMessage<T>(token, _workerManager.TryAck), linkedToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!outerCancellationToken.IsCancellationRequested && linkedToken.IsCancellationRequested)
        {
            _workerManager.Logger.LogWarning(
                "Worker {WorkerId} handling deliveryTag {DeliveryTag} timed out after {Timeout}.",
                _endpoint.Id,
                token.DeliveryTag,
                _endpoint.HandlerTimeout);
            throw;
        }
    }
}

