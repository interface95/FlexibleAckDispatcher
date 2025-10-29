using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Grpc.Core;
using GrpcDotNetNamedPipes;
using InMemoryWorkerBalancer.Remote.Protos;

namespace InMemoryWorkerBalancer.Remote.NamedPipe;

/// <summary>
/// 基于 <c>GrpcDotNetNamedPipes</c> 的远程 Worker 桥接器，实现任务下发、ACK/NACK 以及心跳管理。
/// </summary>
public sealed class NamedPipeRemoteWorkerBridge : IRemoteWorkerBridge
{
    private readonly NamedPipeRemoteWorkerOptions _options;
    private readonly ConcurrentDictionary<int, SessionState> _sessions = new();
    private readonly object _syncRoot = new();

    private NamedPipeServer? _server;
    private int _workerIdSeed;
    private volatile bool _disposed;

    /// <summary>
    /// 初始化 <see cref="NamedPipeRemoteWorkerBridge"/>，使用指定的命名管道参数。
    /// </summary>
    /// <param name="options">命名管道桥接配置。</param>
    public NamedPipeRemoteWorkerBridge(NamedPipeRemoteWorkerOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    public event Func<RemoteWorkerSession, ValueTask>? WorkerConnected;

    /// <inheritdoc />
    public event Func<RemoteWorkerSession, ValueTask>? WorkerDisconnected;

    /// <inheritdoc />
    public event Func<RemoteTaskAcknowledgement, ValueTask>? TaskAcknowledged;

    /// <inheritdoc />
    public event Func<RemoteTaskRejection, ValueTask>? TaskRejected;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        lock (_syncRoot)
        {
            if (_server is not null)
            {
                return Task.CompletedTask;
            }

            var server = new NamedPipeServer(_options.PipeName);
            WorkerHub.BindService(server.ServiceBinder, new NamedPipeWorkerHubService(this));
            _server = server;
            server.Start();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        NamedPipeServer? serverToStop;
        lock (_syncRoot)
        {
            serverToStop = _server;
            _server = null;
        }

        if (serverToStop is not null)
        {
            serverToStop.Kill();
            serverToStop.Dispose();
        }

        var workerIds = _sessions.Keys.ToArray();
        foreach (var workerId in workerIds)
        {
            await RemoveSessionAsync(workerId, raiseDisconnected: true).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask DispatchAsync(RemoteTaskDispatch dispatch, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (dispatch.Session is null)
        {
            throw new ArgumentNullException(nameof(dispatch.Session));
        }

        if (!_sessions.TryGetValue(dispatch.Session.WorkerId, out var state))
        {
            throw new InvalidOperationException($"Worker {dispatch.Session.WorkerId} is not registered.");
        }

        using var linked = CreateLinkedToken(dispatch.CancellationToken, cancellationToken, out var token);

        var lockAcquired = false;
        try
        {
            await state.WaitForStreamReadyAsync(token).ConfigureAwait(false);
            var writer = state.Stream ?? throw new InvalidOperationException($"Worker {dispatch.Session.WorkerId} does not have an active subscription.");
            await state.SendLock.WaitAsync(token).ConfigureAwait(false);
            lockAcquired = true;

            var envelope = dispatch.Envelope;
            var message = new DispatcherMessage
            {
                Task = new TaskMessage
                {
                    DeliveryTag = envelope.DeliveryTag,
                    MessageType = envelope.MessageType,
                    Payload = envelope.Payload.IsEmpty ? ByteString.Empty : ByteString.CopyFrom(envelope.Payload.Span)
                }
            };

            if (envelope.Headers is { Count: > 0 })
            {
                foreach (var header in envelope.Headers)
                {
                    message.Task.Headers.Add(header.Key, header.Value);
                }
            }

            await writer.WriteAsync(message).ConfigureAwait(false);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled)
        {
            throw new OperationCanceledException("Dispatcher stream cancelled.", ex, token);
        }
        finally
        {
            if (lockAcquired)
            {
                state.SendLock.Release();
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await StopAsync().ConfigureAwait(false);
    }

    internal async Task<RegisterReply> HandleRegisterAsync(RegisterRequest request, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        var workerId = Interlocked.Increment(ref _workerIdSeed);
        var descriptor = CreateDescriptor(request, workerId);
        var session = new RemoteWorkerSession(workerId, descriptor);
        var state = new SessionState(session);

        if (!_sessions.TryAdd(workerId, state))
        {
            throw new RpcException(new Status(StatusCode.Internal, $"Failed to register worker {workerId}."));
        }

        await InvokeHandlersAsync(WorkerConnected, session).ConfigureAwait(false);

        return new RegisterReply { WorkerId = workerId };
    }

    internal Task HandleSubscribeAsync(int workerId, IServerStreamWriter<DispatcherMessage> responseStream, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        if (!_sessions.TryGetValue(workerId, out var state))
            throw new RpcException(new Status(StatusCode.NotFound, $"Worker {workerId} is not registered."));

        return state.AttachStreamAsync(responseStream, cancellationToken, () => ScheduleRemoval(workerId, raiseDisconnected: true));
    }

    internal Task HandleAckAsync(int workerId, long deliveryTag)
    {
        if (!_sessions.ContainsKey(workerId))
            throw new RpcException(new Status(StatusCode.NotFound, $"Worker {workerId} is not registered."));

        return InvokeHandlersAsync(TaskAcknowledged, new RemoteTaskAcknowledgement(workerId, deliveryTag));
    }

    internal Task HandleNackAsync(int workerId, long deliveryTag, bool requeue, string? reason)
    {
        if (!_sessions.ContainsKey(workerId))
            throw new RpcException(new Status(StatusCode.NotFound, $"Worker {workerId} is not registered."));

        return InvokeHandlersAsync(TaskRejected, new RemoteTaskRejection(workerId, deliveryTag, requeue, reason));
    }

    internal Task HandleHeartbeatAsync(int workerId)
    {
        if (!_sessions.TryGetValue(workerId, out var state))
            throw new RpcException(new Status(StatusCode.NotFound, $"Worker {workerId} is not registered."));

        state.Session.LastHeartbeat = DateTimeOffset.UtcNow;
        return Task.CompletedTask;
    }

    private RemoteWorkerDescriptor CreateDescriptor(RegisterRequest request, int workerId)
    {
        var name = string.IsNullOrWhiteSpace(request.WorkerName)
            ? $"remote-worker-{workerId}"
            : request.WorkerName;

        var prefetch = request.Prefetch > 0 ? request.Prefetch : SubscriptionOptions.DefaultPrefetch;
        prefetch = Math.Clamp(prefetch, 1, 10_000);

        var concurrency = request.ConcurrencyLimit > 0 ? request.ConcurrencyLimit : prefetch;
        concurrency = Math.Min(concurrency, SubscriptionOptions.MaxConcurrencyLimit);
        concurrency = Math.Min(concurrency, prefetch);

        var metadata = request.Metadata.Count > 0
            ? new Dictionary<string, string>(request.Metadata)
            : new Dictionary<string, string>();

        return new RemoteWorkerDescriptor
        {
            WorkerName = name,
            Prefetch = prefetch,
            ConcurrencyLimit = concurrency,
            Metadata = metadata
        };
    }

    private void ScheduleRemoval(int workerId, bool raiseDisconnected)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await RemoveSessionAsync(workerId, raiseDisconnected).ConfigureAwait(false);
            }
            catch
            {
                // Intentionally swallow exceptions triggered during cancellation cleanup.
            }
        });
    }

    private Task RemoveSessionAsync(int workerId, bool raiseDisconnected)
    {
        if (!_sessions.TryRemove(workerId, out var state))
            return Task.CompletedTask;

        state.Dispose();

        if (!raiseDisconnected)
            return Task.CompletedTask;

        return InvokeHandlersAsync(WorkerDisconnected, state.Session);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(NamedPipeRemoteWorkerBridge));
    }

    private static async Task InvokeHandlersAsync<T>(Func<T, ValueTask>? handlers, T argument)
    {
        if (handlers is null)
            return;

        foreach (var handler in handlers.GetInvocationList().Cast<Func<T, ValueTask>>())
        {
            await handler(argument).ConfigureAwait(false);
        }
    }

    private static IDisposable CreateLinkedToken(CancellationToken first, CancellationToken second, out CancellationToken token)
    {
        if (!first.CanBeCanceled && !second.CanBeCanceled)
        {
            token = CancellationToken.None;
            return NullDisposable.Instance;
        }

        if (!first.CanBeCanceled)
        {
            token = second;
            return NullDisposable.Instance;
        }

        if (!second.CanBeCanceled)
        {
            token = first;
            return NullDisposable.Instance;
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(first, second);
        token = cts.Token;
        return cts;
    }

    private sealed class SessionState : IDisposable
    {
        private readonly object _streamLock = new();
        private CancellationTokenRegistration _subscriptionRegistration;
        private TaskCompletionSource<object?> _streamCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource<object?> _streamReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _disposed;

        public SessionState(RemoteWorkerSession session)
        {
            Session = session;
            SendLock = new SemaphoreSlim(1, 1);
        }

        public RemoteWorkerSession Session { get; }

        public IServerStreamWriter<DispatcherMessage>? Stream { get; private set; }

        public SemaphoreSlim SendLock { get; }

        public Task WaitForStreamReadyAsync(CancellationToken cancellationToken)
        {
            if (Stream is not null)
                return Task.CompletedTask;

            return _streamReady.Task.WaitAsync(cancellationToken);
        }

        public Task AttachStreamAsync(IServerStreamWriter<DispatcherMessage> stream, CancellationToken cancellationToken, Action onCanceled)
        {
            lock (_streamLock)
            {
                if (_disposed)
                    throw new ObjectDisposedException(nameof(SessionState));

                if (Stream is not null)
                    throw new InvalidOperationException($"Worker {Session.WorkerId} already has an active subscription.");

                Stream = stream;
                _streamCompletion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
                _streamReady.TrySetResult(null);
                _subscriptionRegistration = cancellationToken.Register(onCanceled);
                return _streamCompletion.Task;
            }
        }

        public void CompleteStream()
        {
            lock (_streamLock)
            {
                if (Stream is null)
                    return;

                _subscriptionRegistration.Dispose();
                _subscriptionRegistration = default;
                var completion = _streamCompletion;
                Stream = null;
                _streamReady = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
                completion.TrySetResult(null);
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            CompleteStream();
            _disposed = true;
            SendLock.Dispose();
        }
    }

    private sealed class NullDisposable : IDisposable
    {
        public static readonly NullDisposable Instance = new();

        private NullDisposable()
        {
        }

        public void Dispose()
        {
        }
    }
}

