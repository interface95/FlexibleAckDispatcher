using System;
using System.Threading;
using System.Threading.Channels;
using FlexibleAckDispatcher.Abstractions;
using Microsoft.Extensions.Logging;

namespace FlexibleAckDispatcher.InMemory.Internal;

internal sealed class WorkerTaskRunner : IWorkerTaskRunner
{
    private readonly WorkerEndpoint _endpoint;
    private readonly ChannelReader<ReadOnlyMemory<byte>> _reader;
    private readonly WorkerProcessingDelegate _handler;
    private readonly WorkerManager _workerManager;
    private int _failureCount;
    private volatile bool _isStopped;
    private DateTimeOffset _currentTaskStartedAt;

    public WorkerTaskRunner(
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

    public bool IsStopped => _isStopped;

    public int FailureCount => Volatile.Read(ref _failureCount);

    public TimeSpan CurrentTaskDuration =>
        _currentTaskStartedAt == default
            ? TimeSpan.Zero
            : DateTimeOffset.UtcNow - _currentTaskStartedAt;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        return Task.Factory.StartNew(
                () => ProcessAsync(cancellationToken),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default).Unwrap();
    }

    private async Task ProcessAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!_isStopped && await _reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (!_isStopped && _reader.TryRead(out var payload))
                {
                    WorkerAckToken? token = null;
                    try
                    {
                        _currentTaskStartedAt = DateTimeOffset.UtcNow;
                        token = await _workerManager.RegisterInFlightAsync(_endpoint, payload, cancellationToken).ConfigureAwait(false);
                        _workerManager.Logger.LogDebug("Worker {WorkerId} handling deliveryTag {DeliveryTag}", _endpoint.Id, token.DeliveryTag);

                        await ExecuteWithTimeoutAsync(token, cancellationToken).ConfigureAwait(false);
                        _workerManager.RegisterAckTimeout(_endpoint, token);
                        _currentTaskStartedAt = default;
                    }
                    catch (Exception ex)
                    {
                        HandleFailure();
                        _currentTaskStartedAt = default;

                        if (token is not null)
                        {
                            _workerManager.ForceRelease(token.DeliveryTag, "handler failure");
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
        catch (Exception ex)
        {
            HandleFailure();
            _workerManager.Logger.LogError(ex, "Unexpected error in worker loop for Worker {WorkerId}", _endpoint.Id);
            throw;
        }
    }

    private async Task ExecuteWithTimeoutAsync(WorkerAckToken token, CancellationToken outerCancellationToken)
    {
        using var scope = ReusableTimeoutScope.Rent(outerCancellationToken, _endpoint.HandlerTimeout, out var linkedToken);

        try
        {
            var context = new WorkerDeliveryContext(token, _workerManager.TryAck, _currentTaskStartedAt);
            await _handler(context, linkedToken).ConfigureAwait(false);
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

    private void HandleFailure()
    {
        var failures = Interlocked.Increment(ref _failureCount);
        if (failures < _endpoint.FailureThreshold || _isStopped)
        {
            return;
        }

        _isStopped = true;
        _workerManager.Logger.LogError(
            "Worker {WorkerId} reached failure threshold {Threshold} and will stop.",
            _endpoint.Id,
            _endpoint.FailureThreshold);

        if (_endpoint.Cancellation.IsCancellationRequested)
        {
            return;
        }

        try
        {
            _endpoint.Cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
