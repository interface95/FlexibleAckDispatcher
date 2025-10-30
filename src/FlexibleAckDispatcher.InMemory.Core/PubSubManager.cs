using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using FlexibleAckDispatcher.Abstractions;
using FlexibleAckDispatcher.InMemory.Core.Internal;
using Microsoft.Extensions.Logging;
using SubscriptionDefaults = FlexibleAckDispatcher.Abstractions.SubscriptionDefaults;

namespace FlexibleAckDispatcher.InMemory.Core;

/// <summary>
/// 发布-订阅管理器，负责调度消息并维护 Worker。
/// </summary>
public sealed class PubSubManager : IPubSubManager
{
    private readonly System.Threading.Channels.Channel<ReadOnlyMemory<byte>> _channel;
    private readonly WorkerManager _workerManager;
    private readonly WorkerDispatcher _dispatcher = new();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly ConcurrentDictionary<int, PubSubSubscription> _subscriptions = new();
    private readonly ConcurrentDictionary<Type, object> _typedChannels = new();
    private readonly ILogger _logger;
    private readonly IWorkerPayloadSerializer _serializer;
    private readonly Task _dispatchTask;
    private readonly SubscriptionDefaults _subscriptionDefaults;
    private readonly IReadOnlyList<Func<PubSubManager, ValueTask>> _disposeActions;
    private int _disposed;

    internal PubSubManager(PubSubManagerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.Serializer is null)
        {
            throw new ArgumentNullException(nameof(options.Serializer), "Serializer must be configured.");
        }

        if (options.Logger is null)
        {
            throw new ArgumentNullException(nameof(options.Logger), "Logger must be configured.");
        }

        _serializer = options.Serializer;
        _logger = options.Logger;

        _channel = System.Threading.Channels.Channel.CreateUnbounded<ReadOnlyMemory<byte>>(new System.Threading.Channels.UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false
        });

        var selectionStrategy = options.SelectionStrategyFactory.Invoke();
        _workerManager = WorkerManagerFactory.CreateManager(_cancellation.Token, _logger, options.AckMonitorInterval, selectionStrategy);
        AttachWorkerLifecycleHandlers(options);
        _subscriptionDefaults = options.BuildSubscriptionDefaults();
        _disposeActions = options.ManagerDisposers;

        foreach (var initializer in options.ManagerInitializers)
        {
            initializer(this);
        }
        
        _dispatchTask = Task.Factory.StartNew(
                () => _dispatcher.ProcessAsync(_channel.Reader, _workerManager, _cancellation.Token),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default)
            .Unwrap();
    }

    /// <summary>
    /// 使用选项回调创建 <see cref="PubSubManager"/>。
    /// </summary>
    public static PubSubManager Create(Action<PubSubManagerOptions>? configure = null)
    {
        var options = new PubSubManagerOptions();
        configure?.Invoke(options);
        options.ApplyModules();
        options.Validate();
        return new PubSubManager(options);
    }

    internal WorkerManager WorkerManager => _workerManager;

    internal SubscriptionDefaults SubscriptionDefaults => _subscriptionDefaults;

    internal ILogger Logger => _logger;

    internal IWorkerPayloadSerializer Serializer => _serializer;

    internal CancellationToken CancellationToken => _cancellation.Token;

    /// <summary>
    /// 当前订阅数量。
    /// </summary>
    public int SubscriberCount => _subscriptions.Count;

    /// <summary>
    /// 当前空闲 Worker 数量。
    /// </summary>
    public int IdleCount => _workerManager.IdleCount;

    /// <summary>
    /// 当前执行中的任务数量（所有 Worker 总和）。
    /// </summary>
    public int RunningCount => _workerManager.RunningCount;

    /// <summary>
    /// 当前未消费的消息数量。
    /// </summary>
    public long PendingCount => _workerManager.PendingCount;

    /// <summary>
    /// 已推送（调度）的任务总数。
    /// </summary>
    public long DispatchedCount => _workerManager.DispatchedCount;

    /// <summary>
    /// 已完成（确认）的任务总数。
    /// </summary>
    public long CompletedCount => _workerManager.CompletedCount;

    /// <summary>
    /// 获取当前所有 Worker 端点的快照。
    /// </summary>
    public IReadOnlyList<WorkerEndpointSnapshot> GetSnapshot()
    {
        return _workerManager.GetSnapshot();
    }

    /// <summary>
    /// 直接发布一条消息。
    /// </summary>
    public ValueTask PublishAsync<T>(T message, CancellationToken cancellationToken = default)
    {
        return Channel<T>().PublishAsync(message, cancellationToken);
    }

    /// <summary>
    /// 注册一个新的订阅者。
    /// </summary>
    public ValueTask<IPubSubSubscription> SubscribeAsync<T>(WorkerProcessingDelegate<T> handler,
        CancellationToken cancellationToken = default)
    {
        return SubscribeAsync(handler, null, cancellationToken);
    }

    /// <summary>
    /// 注册一个新的订阅者。
    /// </summary>
    public ValueTask<IPubSubSubscription> SubscribeAsync<T>(IWorkerMessageHandler<T> handler,
        CancellationToken cancellationToken = default)
    {
        if (handler is null)
        {
            throw new ArgumentNullException(nameof(handler));
        }

        return SubscribeAsync<T>(handler.HandleAsync, null, cancellationToken);
    }

    /// <summary>
    /// 注册一个新的订阅者。
    /// </summary>
    public async ValueTask<IPubSubSubscription> SubscribeAsync<T>(
        WorkerProcessingDelegate<T> handler,
        Action<SubscriptionOptions>? configure,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var options = SubscriptionOptions.Create(_subscriptionDefaults);
        configure?.Invoke(options);

        var endpoint = await _workerManager.AddWorkerAsync(CreateHandler(handler), options).ConfigureAwait(false);
        var subscription = new PubSubSubscription(this, endpoint.Id);

        if (!_subscriptions.TryAdd(subscription.Id, subscription))
        {
            await _workerManager.RemoveWorkerAsync(endpoint.Id).ConfigureAwait(false);
            throw new InvalidOperationException("Failed to register subscription.");
        }

        _logger.LogInformation("Subscription {SubscriptionId} registered", subscription.Id);
        return subscription;
    }

    /// <summary>
    /// 注册一个新的订阅者。
    /// </summary>
    public ValueTask<IPubSubSubscription> SubscribeAsync<T>(
        IWorkerMessageHandler<T> handler,
        Action<SubscriptionOptions>? configure,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);

        return SubscribeAsync<T>(handler.HandleAsync, configure, cancellationToken);
    }

    /// <summary>
    /// 释放所有资源，停止调度与 Worker。
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        await _cancellation.CancelAsync().ConfigureAwait(false);
        _channel.Writer.TryComplete();

        foreach (var subscription in _subscriptions.Values)
        {
            await subscription.DisposeAsync().ConfigureAwait(false);
        }

        foreach (var disposer in _disposeActions)
        {
            await disposer(this).ConfigureAwait(false);
        }

        try
        {
            await _dispatchTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        await _workerManager.StopAllAsync().ConfigureAwait(false);
        _cancellation.Dispose();
    }

    /// <summary>
    /// 根据 deliveryTag 手动确认消息。
    /// </summary>
    public ValueTask AckAsync(long deliveryTag, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_workerManager.TryAck(deliveryTag))
            return ValueTask.CompletedTask;

        throw new InvalidOperationException($"未找到待确认的消息，deliveryTag={deliveryTag}");
    }

    internal async Task RemoveSubscriptionAsync(int subscriptionId)
    {
        if (_subscriptions.TryRemove(subscriptionId, out _))
    {
            await _workerManager.RemoveWorkerAsync(subscriptionId).ConfigureAwait(false);
            _logger.LogInformation("Subscription {SubscriptionId} removed", subscriptionId);
        }
    }

    private IPubSubChannel<T> Channel<T>()
    {
        return (IPubSubChannel<T>)_typedChannels.GetOrAdd(typeof(T),
            _ => new TypedPubSubChannel<T>(_channel.Writer, _serializer, _workerManager));
    }
    
    private void AttachWorkerLifecycleHandlers(PubSubManagerOptions options)
    {
        foreach (var handler in options.WorkerAddedHandlers)
        {
            _workerManager.WorkerAdded += handler;
        }

        foreach (var handler in options.WorkerRemovedHandlers)
        {
            _workerManager.WorkerRemoved += handler;
        }
    }

    private WorkerProcessingDelegate CreateHandler<T>(WorkerProcessingDelegate<T> handler)
    {
        return async (context, cancellationToken) =>
        {
            var payload = _serializer.Deserialize<T>(context.Payload);
            var typedMessage = new WorkerMessage<T>(context, payload);
            await handler(typedMessage, cancellationToken).ConfigureAwait(false);
        };
    }
}