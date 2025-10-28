using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace InMemoryWorkerBalancer.Internal;

internal sealed class AckTimeoutScheduler
{
    private readonly WorkerManager _workerManager;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<long, CancellationTokenSource> _registrations = new();

    public AckTimeoutScheduler(WorkerManager workerManager, ILogger logger)
    {
        _workerManager = workerManager;
        _logger = logger;
    }

    public void Schedule(WorkerEndpoint endpoint, WorkerAckToken token)
    {
        if (endpoint.AckTimeout is null)
        {
            return;
        }

        var timeout = endpoint.AckTimeout.Value;
        var cts = new CancellationTokenSource();
        if (!_registrations.TryAdd(token.DeliveryTag, cts))
        {
            cts.Dispose();
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(timeout, cts.Token).ConfigureAwait(false);
                if (!cts.IsCancellationRequested)
                {
                    await _workerManager.HandleAckTimeoutAsync(endpoint, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                _registrations.TryRemove(token.DeliveryTag, out _);
                cts.Dispose();
            }
        });
    }

    public void Cancel(long deliveryTag)
    {
        if (_registrations.TryRemove(deliveryTag, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    public void Reset()
    {
        foreach (var kvp in _registrations)
        {
            if (_registrations.TryRemove(kvp.Key, out var cts))
            {
                cts.Cancel();
                cts.Dispose();
            }
        }
    }
}
