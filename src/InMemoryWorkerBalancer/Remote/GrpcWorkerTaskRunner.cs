using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using InMemoryWorkerBalancer.Internal;
using Microsoft.Extensions.Logging;

namespace InMemoryWorkerBalancer.Remote;

internal sealed class GrpcWorkerTaskRunner : IWorkerTaskRunner
{
    private readonly WorkerEndpoint _endpoint;
    private readonly ChannelReader<ReadOnlyMemory<byte>> _reader;
    private readonly WorkerManager _workerManager;
    private readonly IRemoteWorkerBridge _bridge;
    private readonly RemoteWorkerContext _context;
    private readonly ILogger _logger;

    private readonly object _tasksLock = new();
    private readonly HashSet<Task> _running = new();

    private int _failureCount;
    private volatile bool _isStopped;
    private DateTimeOffset _currentTaskStartedAt;

    public GrpcWorkerTaskRunner(
        WorkerEndpoint endpoint,
        ChannelReader<ReadOnlyMemory<byte>> reader,
        WorkerManager workerManager,
        IRemoteWorkerBridge bridge,
        RemoteWorkerContext context,
        ILogger logger)
    {
        _endpoint = endpoint;
        _reader = reader;
        _workerManager = workerManager;
        _bridge = bridge;
        _context = context;
        _logger = logger;
    }

    public bool IsStopped => _isStopped;

    public int FailureCount => Volatile.Read(ref _failureCount);

    public TimeSpan CurrentTaskDuration =>
        _currentTaskStartedAt == default
            ? TimeSpan.Zero
            : DateTimeOffset.UtcNow - _currentTaskStartedAt;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        return Task.Run(() => PumpAsync(cancellationToken), cancellationToken);
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var payload in _reader.ReadAllAsync(cancellationToken))
            {
                var task = ProcessMessageAsync(payload, cancellationToken);
                lock (_tasksLock)
                {
                    _running.Add(task);
                }

                _ = task.ContinueWith(t =>
                {
                    lock (_tasksLock)
                    {
                        _running.Remove(t);
                    }
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }

        Task[] pending;
        lock (_tasksLock)
        {
            pending = _running.ToArray();
            _running.Clear();
        }

        await Task.WhenAll(pending).ConfigureAwait(false);
    }

    private async Task ProcessMessageAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        WorkerAckToken? token = null;
        try
        {
            _currentTaskStartedAt = DateTimeOffset.UtcNow;
            token = await _workerManager.RegisterInFlightAsync(_endpoint, payload, cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("Worker {WorkerId} handling deliveryTag {DeliveryTag}", _endpoint.Id, token.DeliveryTag);

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var state = new RemoteInFlightState(token.DeliveryTag, payload, _endpoint, tcs);

            if (!_context.InFlight.TryAdd(token.DeliveryTag, state))
            {
                throw new InvalidOperationException($"DeliveryTag {token.DeliveryTag} is already tracked for remote worker {_context.Session.WorkerId}.");
            }

            await using var registration = cancellationToken.Register(() =>
            {
                if (!_context.InFlight.TryRemove(token.DeliveryTag, out var removed))
                    return;
                
                removed.Completion.TrySetCanceled(cancellationToken);
                _workerManager.ForceRelease(token.DeliveryTag, "remote handler cancelled");
            });

            var envelope = new RemoteTaskEnvelope(
                token.DeliveryTag,
                _context.MessageType,
                payload,
                _context.Headers);

            await _bridge.DispatchAsync(new RemoteTaskDispatch(_context.Session, envelope, cancellationToken), cancellationToken).ConfigureAwait(false);
            _workerManager.RegisterAckTimeout(_endpoint, token);
            await tcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // cancellation expected when shutting down
        }
        catch (Exception ex)
        {
            HandleFailure();

            if (token is not null)
            {
                _workerManager.ForceRelease(token.DeliveryTag, "remote handler failure");
            }
            else if (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await _workerManager.ReturnPayloadAsync(_endpoint, payload, cancellationToken).ConfigureAwait(false);
                }
                catch (ChannelClosedException)
                {
                    _logger.LogWarning("Worker {WorkerId} channel closed while returning payload", _endpoint.Id);
                }
            }

            _logger.LogError(ex, "Worker {WorkerId} handling failed", _endpoint.Id);
        }
        finally
        {
            _currentTaskStartedAt = default;
        }
    }

    private void HandleFailure()
    {
        var failures = Interlocked.Increment(ref _failureCount);
        if (failures < _endpoint.FailureThreshold || _isStopped)
            return;

        _isStopped = true;
        _logger.LogError(
            "Worker {WorkerId} reached failure threshold {Threshold} and will stop.",
            _endpoint.Id,
            _endpoint.FailureThreshold);

        if (_endpoint.Cancellation.IsCancellationRequested)
            return;

        try
        {
            _endpoint.Cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }
}