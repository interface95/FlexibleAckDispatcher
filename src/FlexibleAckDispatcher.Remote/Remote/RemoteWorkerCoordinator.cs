using System;
using System.Collections.Concurrent;
using System.Globalization;
using FlexibleAckDispatcher.Abstractions;
using FlexibleAckDispatcher.Abstractions.Remote;
using FlexibleAckDispatcher.InMemory.Internal;
using Microsoft.Extensions.Logging;

namespace FlexibleAckDispatcher.Remote;

internal sealed class RemoteWorkerCoordinator : IAsyncDisposable
{
    private readonly WorkerManager _workerManager;
    private readonly IRemoteWorkerBridge _bridge;
    private readonly ILogger _logger;
    private readonly SubscriptionDefaults _defaults;

    private readonly ConcurrentDictionary<int, RemoteWorkerContext> _sessions = new();
    private readonly Func<RemoteWorkerSession, ValueTask> _workerConnectedHandler;
    private readonly Func<RemoteWorkerSession, ValueTask> _workerDisconnectedHandler;
    private readonly Func<RemoteTaskAcknowledgement, ValueTask> _taskAcknowledgedHandler;
    private readonly Func<RemoteTaskRejection, ValueTask> _taskRejectedHandler;

    public RemoteWorkerCoordinator(WorkerManager workerManager, IRemoteWorkerBridge bridge, ILogger logger,
        SubscriptionDefaults defaults)
    {
        _workerManager = workerManager;
        _bridge = bridge;
        _logger = logger;
        _defaults = defaults;

        _workerConnectedHandler = OnWorkerConnectedAsync;
        _workerDisconnectedHandler = OnWorkerDisconnectedAsync;
        _taskAcknowledgedHandler = OnTaskAcknowledgedAsync;
        _taskRejectedHandler = OnTaskRejectedAsync;

        _bridge.WorkerConnected += _workerConnectedHandler;
        _bridge.WorkerDisconnected += _workerDisconnectedHandler;
        _bridge.TaskAcknowledged += _taskAcknowledgedHandler;
        _bridge.TaskRejected += _taskRejectedHandler;
    }

    public async ValueTask DisposeAsync()
    {
        _bridge.WorkerConnected -= _workerConnectedHandler;
        _bridge.WorkerDisconnected -= _workerDisconnectedHandler;
        _bridge.TaskAcknowledged -= _taskAcknowledgedHandler;
        _bridge.TaskRejected -= _taskRejectedHandler;

        foreach (var context in _sessions.Values)
        {
            await SafeRemoveContextAsync(context).ConfigureAwait(false);
        }

        _sessions.Clear();
    }

    private async ValueTask OnWorkerConnectedAsync(RemoteWorkerSession session)
    {
        try
        {
            var context = await RegisterRemoteWorkerAsync(session).ConfigureAwait(false);
            _sessions[session.WorkerId] = context;
            _logger.LogInformation("Remote worker {WorkerId} registered as endpoint {EndpointId}", session.WorkerId,
                context.Endpoint.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to register remote worker {WorkerId}", session.WorkerId);
        }
    }

    private async ValueTask OnWorkerDisconnectedAsync(RemoteWorkerSession session)
    {
        if (!_sessions.TryRemove(session.WorkerId, out var context))
        {
            return;
        }

        await SafeRemoveContextAsync(context).ConfigureAwait(false);
        _logger.LogInformation("Remote worker {WorkerId} disconnected", session.WorkerId);
    }

    private ValueTask OnTaskAcknowledgedAsync(RemoteTaskAcknowledgement acknowledgement)
    {
        if (!_sessions.TryGetValue(acknowledgement.WorkerId, out var context))
        {
            return ValueTask.CompletedTask;
        }

        if (!context.InFlight.TryRemove(acknowledgement.DeliveryTag, out var state))
        {
            return ValueTask.CompletedTask;
        }

        if (!_workerManager.TryAck(acknowledgement.DeliveryTag))
        {
            _logger.LogWarning("Ack for deliveryTag {DeliveryTag} could not be applied", acknowledgement.DeliveryTag);
        }

        state.Completion.TrySetResult(true);
        return ValueTask.CompletedTask;
    }

    private async ValueTask OnTaskRejectedAsync(RemoteTaskRejection rejection)
    {
        if (!_sessions.TryGetValue(rejection.WorkerId, out var context))
        {
            return;
        }

        if (!context.InFlight.TryRemove(rejection.DeliveryTag, out var state))
        {
            return;
        }

        _workerManager.ForceRelease(rejection.DeliveryTag, rejection.Reason);

        if (rejection.Requeue)
        {
            try
            {
                await _workerManager.ReturnPayloadAsync(state.Endpoint, state.Payload, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to return payload for deliveryTag {DeliveryTag}", rejection.DeliveryTag);
            }
        }

        state.Completion.TrySetResult(true);
    }

    private async Task<RemoteWorkerContext> RegisterRemoteWorkerAsync(RemoteWorkerSession session)
    {
        var options = SubscriptionOptions.Create(_defaults)
            .WithPrefetch(session.Descriptor.Prefetch)
            .WithConcurrencyLimit(session.Descriptor.ConcurrencyLimit)
            .WithName(session.Descriptor.WorkerName);

        if (session.Descriptor.Metadata is { Count: > 0 })
        {
            if (session.Descriptor.Metadata.TryGetValue(RemoteWorkerMetadataKeys.FailureThreshold, out var failureText) &&
                int.TryParse(failureText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var failureThreshold))
            {
                try
                {
                    options.WithFailureThreshold(failureThreshold);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Remote worker {WorkerId} provided invalid failure threshold: {Value}", session.WorkerId,
                        failureText);
                }
            }

            if (session.Descriptor.Metadata.TryGetValue(RemoteWorkerMetadataKeys.HandlerTimeoutTicks, out var timeoutText) &&
                long.TryParse(timeoutText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var timeoutTicks))
            {
                try
                {
                    options.WithHandlerTimeout(TimeSpan.FromTicks(timeoutTicks));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Remote worker {WorkerId} provided invalid handler timeout ticks: {Value}", session.WorkerId,
                        timeoutText);
                }
            }
        }

        options.Validate();

        var context = new RemoteWorkerContext(session);
        var factory = new GrpcWorkerTaskRunnerFactory(_bridge, context, _logger);
        var endpoint = await _workerManager.AddWorkerAsync((_, _) => Task.CompletedTask, options, factory)
            .ConfigureAwait(false);
        context.Endpoint = endpoint;
        return context;
    }

    private async Task SafeRemoveContextAsync(RemoteWorkerContext context)
    {
        foreach (var entry in context.InFlight)
        {
            _workerManager.ForceRelease(entry.Key, "remote worker disconnected");
            entry.Value.Completion.TrySetCanceled();
        }

        context.InFlight.Clear();

        try
        {
            await _workerManager.RemoveWorkerAsync(context.Endpoint.Id).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove remote worker endpoint {EndpointId}", context.Endpoint.Id);
        }
    }
}

