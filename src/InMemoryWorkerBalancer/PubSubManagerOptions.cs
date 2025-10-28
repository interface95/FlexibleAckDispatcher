using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using InMemoryWorkerBalancer.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace InMemoryWorkerBalancer;

/// <summary>
/// 使用构建者模式配置 <see cref="PubSubManager"/>。
/// </summary>
public sealed class PubSubManagerOptions
{
    private readonly List<Func<WorkerEndpointSnapshot, Task>> _workerAddedHandlers = new();
    private readonly List<Func<WorkerEndpointSnapshot, Task>> _workerRemovedHandlers = new();
    private IWorkerPayloadSerializer _serializer = JsonWorkerPayloadSerializer.Default;
    private ILogger _logger = NullLogger.Instance;
    private TimeSpan? _ackMonitorInterval;
    private int? _defaultPrefetch;
    private int? _defaultConcurrencyLimit;
    private TimeSpan? _defaultHandlerTimeout;
    private int? _defaultFailureThreshold;
    private TimeSpan? _defaultAckTimeout;

    /// <summary>
    /// 配置自定义序列化器，默认使用 <see cref="JsonWorkerPayloadSerializer.Default"/>。
    /// </summary>
    public PubSubManagerOptions WithSerializer(IWorkerPayloadSerializer serializer)
    {
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        return this;
    }

    /// <summary>
    /// 配置自定义日志记录器，默认使用 <see cref="Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance"/>。
    /// </summary>
    public PubSubManagerOptions WithLogger(ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        return this;
    }

    /// <summary>
    /// 注册 Worker 添加事件处理器。
    /// </summary>
    public PubSubManagerOptions OnWorkerAddedHandler(Func<WorkerEndpointSnapshot, Task> handler)
    {
        if (handler is null)
        {
            throw new ArgumentNullException(nameof(handler));
        }

        _workerAddedHandlers.Add(handler);
        return this;
    }

    /// <summary>
    /// 注册 Worker 移除事件处理器。
    /// </summary>
    public PubSubManagerOptions OnWorkerRemovedHandler(Func<WorkerEndpointSnapshot, Task> handler)
    {
        if (handler is null)
        {
            throw new ArgumentNullException(nameof(handler));
        }

        _workerRemovedHandlers.Add(handler);
        return this;
    }

    /// <summary>
    /// 配置 ACK 定时检测的全局轮询间隔；默认值为 <c>null</c>，表示按订阅的 AckTimeout 自动推导。
    /// </summary>
    public PubSubManagerOptions WithAckMonitorInterval(TimeSpan? interval)
    {
        if (interval.HasValue && interval.Value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval), "Ack monitor interval must be greater than zero.");
        }

        _ackMonitorInterval = interval;
        return this;
    }

    /// <summary>
    /// 配置默认的 Prefetch 数量，供未显式指定的订阅使用。
    /// </summary>
    public PubSubManagerOptions WithDefaultPrefetch(int prefetch)
    {
        if (prefetch <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(prefetch), "Prefetch must be greater than zero.");
        }

        _defaultPrefetch = prefetch;
        return this;
    }

    /// <summary>
    /// 配置默认的并发上限。
    /// </summary>
    public PubSubManagerOptions WithDefaultConcurrencyLimit(int limit)
    {
        if (limit <= 0 || limit > SubscriptionOptions.MaxConcurrencyLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(limit),
                $"Concurrency limit must be between 1 and {SubscriptionOptions.MaxConcurrencyLimit}.");
        }

        _defaultConcurrencyLimit = limit;
        return this;
    }

    /// <summary>
    /// 配置默认的处理超时时间。
    /// </summary>
    public PubSubManagerOptions WithDefaultHandlerTimeout(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Handler timeout must be greater than zero.");
        }

        _defaultHandlerTimeout = timeout;
        return this;
    }

    /// <summary>
    /// 配置默认的失败阈值。
    /// </summary>
    public PubSubManagerOptions WithDefaultFailureThreshold(int threshold)
    {
        if (threshold <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(threshold), "Failure threshold must be greater than zero.");
        }

        _defaultFailureThreshold = threshold;
        return this;
    }

    /// <summary>
    /// 配置默认的 ACK 超时时间。
    /// </summary>
    public PubSubManagerOptions WithDefaultAckTimeout(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Ack timeout must be greater than zero.");
        }

        _defaultAckTimeout = timeout;
        return this;
    }

    internal IWorkerPayloadSerializer Serializer => _serializer;

    internal ILogger Logger => _logger;

    internal IReadOnlyList<Func<WorkerEndpointSnapshot, Task>> WorkerAddedHandlers => _workerAddedHandlers;

    internal IReadOnlyList<Func<WorkerEndpointSnapshot, Task>> WorkerRemovedHandlers => _workerRemovedHandlers;

    internal TimeSpan? AckMonitorInterval => _ackMonitorInterval;

    internal SubscriptionDefaults BuildSubscriptionDefaults() => new()
    {
        Prefetch = _defaultPrefetch,
        ConcurrencyLimit = _defaultConcurrencyLimit,
        HandlerTimeout = _defaultHandlerTimeout,
        FailureThreshold = _defaultFailureThreshold,
        AckTimeout = _defaultAckTimeout
    };

    internal void Validate()
    {
        if (_serializer is null)
        {
            throw new InvalidOperationException("Serializer must be provided.");
        }

        if (_logger is null)
        {
            throw new InvalidOperationException("Logger must be provided.");
        }
    }
}

