using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using WorkerBalancer.Abstractions;
using WorkerBalancer.Internal;

namespace WorkerBalancer;

/// <summary>
/// 发布-订阅管理器，负责调度消息并维护 Worker。
/// </summary>
public sealed class PubSubManager<T> : IPubSubManager<T>
{
    private readonly Channel<T> _channel;
    private readonly PubSubChannel _pubChannel;
    private readonly WorkerManager<T> _workerManager;
    private readonly WorkerDispatcher<T> _dispatcher = new();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly ConcurrentDictionary<int, PubSubSubscription> _subscriptions = new();
    private readonly ILogger _logger;
    private readonly Task _dispatchTask;
    private int _disposed;

    /// <summary>
    /// 初始化发布订阅管理器。
    /// </summary>
    public PubSubManager(ILogger? logger = null)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        _channel = System.Threading.Channels.Channel.CreateUnbounded<T>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false
        });

        _pubChannel = new PubSubChannel(_channel.Writer);
        _workerManager = WorkerManagerFactory.CreateManager<T>(_cancellation.Token, _logger);
        _dispatchTask = Task.Factory.StartNew(
                () => _dispatcher.ProcessAsync(_channel.Reader, _workerManager, _cancellation.Token),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default)
            .Unwrap();
    }

    /// <summary>
    /// 对外暴露的发布通道。
    /// </summary>
    public IPubSubChannel<T> Channel => _pubChannel;

    /// <summary>
    /// 当前订阅数量。
    /// </summary>
    public int SubscriberCount => _subscriptions.Count;

    /// <summary>
    /// 注册一个新的订阅者。
    /// </summary>
    public async ValueTask<IPubSubSubscription> SubscribeAsync(WorkerProcessingDelegate<T> handler,
        CancellationToken cancellationToken = default)
    {
        return await SubscribeAsync(handler, null, cancellationToken);
    }

    public ValueTask<IPubSubSubscription> SubscribeAsync(IWorkerMessageHandler<T> handler,
        CancellationToken cancellationToken = default)
    {
        if (handler is null)
        {
            throw new ArgumentNullException(nameof(handler));
        }

        return SubscribeAsync(handler.HandleAsync, null, cancellationToken);
    }

    /// <summary>
    /// 注册一个新的订阅者。
    /// </summary>
    /// <param name="handler"></param>
    /// <param name="configure"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    public async ValueTask<IPubSubSubscription> SubscribeAsync(
        WorkerProcessingDelegate<T> handler,
        Action<SubscriptionOptions>? configure,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var options = SubscriptionOptions.Create();
        configure?.Invoke(options);

        var endpoint = _workerManager.AddWorker(handler, options);
        var subscription = new PubSubSubscription(this, endpoint.Id);

        if (!_subscriptions.TryAdd(subscription.Id, subscription))
        {
            await _workerManager.RemoveWorkerAsync(endpoint.Id).ConfigureAwait(false);
            throw new InvalidOperationException("Failed to register subscription.");
        }

        _logger.LogInformation("Subscription {SubscriptionId} registered", subscription.Id);
        return subscription;
    }

    public ValueTask<IPubSubSubscription> SubscribeAsync(
        IWorkerMessageHandler<T> handler,
        Action<SubscriptionOptions>? configure,
        CancellationToken cancellationToken = default)
    {
        if (handler is null)
        {
            throw new ArgumentNullException(nameof(handler));
        }

        return SubscribeAsync(handler.HandleAsync, configure, cancellationToken);
    }

    /// <summary>
    /// 释放所有资源，停止调度与 Worker。
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _cancellation.CancelAsync().ConfigureAwait(false);
        _channel.Writer.TryComplete();

        foreach (var subscription in _subscriptions.Values)
        {
            await subscription.DisposeAsync().ConfigureAwait(false);
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
        {
            return ValueTask.CompletedTask;
        }

        throw new InvalidOperationException($"未找到待确认的消息，deliveryTag={deliveryTag}");
    }

    private async Task RemoveSubscriptionAsync(int subscriptionId)
    {
        if (_subscriptions.TryRemove(subscriptionId, out _))
        {
            await _workerManager.RemoveWorkerAsync(subscriptionId).ConfigureAwait(false);
            _logger.LogInformation("Subscription {SubscriptionId} removed", subscriptionId);
        }
    }

    /// <summary>
    /// 内部发布通道实现。
    /// </summary>
    private sealed class PubSubChannel : IPubSubChannel<T>
    {
        private readonly ChannelWriter<T> _writer;

        public PubSubChannel(ChannelWriter<T> writer)
        {
            _writer = writer;
        }

        public ValueTask PublishAsync(T message, CancellationToken cancellationToken = default)
        {
            return _writer.WriteAsync(message, cancellationToken);
        }
    }

    /// <summary>
    /// 内部订阅句柄，实现释放逻辑。
    /// </summary>
    private sealed class PubSubSubscription : IPubSubSubscription
    {
        private readonly PubSubManager<T> _owner;
        private int _disposed;

        public PubSubSubscription(PubSubManager<T> owner, int id)
        {
            _owner = owner;
            Id = id;
        }

        public int Id { get; }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            await _owner.RemoveSubscriptionAsync(Id).ConfigureAwait(false);
        }
    }
}