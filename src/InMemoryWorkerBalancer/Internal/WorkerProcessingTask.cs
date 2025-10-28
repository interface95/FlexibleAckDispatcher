using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace InMemoryWorkerBalancer.Internal;

internal sealed class WorkerProcessingTask
{
    private readonly WorkerEndpoint _endpoint;
    private readonly ChannelReader<ReadOnlyMemory<byte>> _reader;
    private readonly WorkerProcessingDelegate _handler;
    private readonly WorkerManager _workerManager;
    private readonly Func<bool> _isStopped;
    private readonly Action _reportFailure;

    public WorkerProcessingTask(
        WorkerEndpoint endpoint,
        ChannelReader<ReadOnlyMemory<byte>> reader,
        WorkerProcessingDelegate handler,
        WorkerManager workerManager,
        Func<bool> isStopped,
        Action reportFailure)
    {
        _endpoint = endpoint;
        _reader = reader;
        _handler = handler;
        _workerManager = workerManager;
        _isStopped = isStopped;
        _reportFailure = reportFailure;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!_isStopped() && await _reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (!_isStopped() && _reader.TryRead(out var payload))
                {
                    WorkerAckToken? token = null;
                    try
                    {
                        token = await _workerManager.RegisterInFlightAsync(_endpoint, payload, cancellationToken).ConfigureAwait(false);
                        _workerManager.Logger.LogDebug("Worker {WorkerId} handling deliveryTag {DeliveryTag}", _endpoint.Id, token.DeliveryTag);

                        await ExecuteWithTimeoutAsync(token, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _reportFailure();
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

    private async Task ExecuteWithTimeoutAsync(WorkerAckToken token, CancellationToken outerCancellationToken)
    {
        using var scope = ReusableTimeoutScope.Rent(outerCancellationToken, _endpoint.HandlerTimeout, out var linkedToken);

        try
        {
            var context = new WorkerDeliveryContext(token, _workerManager.TryAck);
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
}
