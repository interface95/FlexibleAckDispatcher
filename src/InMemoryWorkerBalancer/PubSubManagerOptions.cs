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
    private Func<IWorkerSelectionStrategy> _selectionStrategyFactory = () => new Internal.PriorityQueueWorkerSelectionStrategy();
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
    /// 配置 Worker 选择策略工厂，默认使用 <see cref="Internal.PriorityQueueWorkerSelectionStrategy"/>。
    /// </summary>
    public PubSubManagerOptions WithSelectionStrategy(Func<IWorkerSelectionStrategy> strategyFactory)
    {
        _selectionStrategyFactory = strategyFactory ?? throw new ArgumentNullException(nameof(strategyFactory));
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

    internal Func<IWorkerSelectionStrategy> SelectionStrategyFactory => _selectionStrategyFactory;

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
        // 1. 验证必需的依赖项
        if (_serializer is null)
        {
            throw new InvalidOperationException("Serializer must be provided.");
        }

        if (_logger is null)
        {
            throw new InvalidOperationException("Logger must be provided.");
        }

        if (_selectionStrategyFactory is null)
        {
            throw new InvalidOperationException("Selection strategy factory must be provided.");
        }

        // 2. 验证 AckMonitorInterval 的合理性
        if (_ackMonitorInterval.HasValue)
        {
            if (_ackMonitorInterval.Value <= TimeSpan.Zero)
            {
                throw new InvalidOperationException(
                    $"AckMonitorInterval ({_ackMonitorInterval.Value}) must be greater than zero.");
            }

            if (_ackMonitorInterval.Value < TimeSpan.FromMilliseconds(10))
            {
                throw new InvalidOperationException(
                    $"AckMonitorInterval ({_ackMonitorInterval.Value}) is too small (min: 10ms). This may cause excessive CPU usage.");
            }

            if (_ackMonitorInterval.Value > TimeSpan.FromHours(1))
            {
                throw new InvalidOperationException(
                    $"AckMonitorInterval ({_ackMonitorInterval.Value}) is too large (max: 1 hour). Messages may not be released timely.");
            }
        }

        // 3. 验证默认 Prefetch 范围
        if (_defaultPrefetch.HasValue)
        {
            if (_defaultPrefetch.Value <= 0)
            {
                throw new InvalidOperationException(
                    $"Default Prefetch ({_defaultPrefetch.Value}) must be greater than zero.");
            }

            if (_defaultPrefetch.Value > 10000)
            {
                throw new InvalidOperationException(
                    $"Default Prefetch ({_defaultPrefetch.Value}) is too large (max: 10000). This may cause memory issues.");
            }
        }

        // 4. 验证默认 ConcurrencyLimit 范围
        if (_defaultConcurrencyLimit.HasValue)
        {
            if (_defaultConcurrencyLimit.Value <= 0)
            {
                throw new InvalidOperationException(
                    $"Default ConcurrencyLimit ({_defaultConcurrencyLimit.Value}) must be greater than zero.");
            }

            if (_defaultConcurrencyLimit.Value > SubscriptionOptions.MaxConcurrencyLimit)
            {
                throw new InvalidOperationException(
                    $"Default ConcurrencyLimit ({_defaultConcurrencyLimit.Value}) cannot exceed {SubscriptionOptions.MaxConcurrencyLimit}.");
            }

            // 如果同时设置了 Prefetch，验证关系
            if (_defaultPrefetch.HasValue && _defaultConcurrencyLimit.Value > _defaultPrefetch.Value)
            {
                throw new InvalidOperationException(
                    $"Default ConcurrencyLimit ({_defaultConcurrencyLimit.Value}) cannot exceed Default Prefetch ({_defaultPrefetch.Value}).");
            }
        }

        // 5. 验证默认 HandlerTimeout 范围
        if (_defaultHandlerTimeout.HasValue)
        {
            if (_defaultHandlerTimeout.Value <= TimeSpan.Zero)
            {
                throw new InvalidOperationException(
                    $"Default HandlerTimeout ({_defaultHandlerTimeout.Value}) must be greater than zero.");
            }

            if (_defaultHandlerTimeout.Value > TimeSpan.FromHours(24))
            {
                throw new InvalidOperationException(
                    $"Default HandlerTimeout ({_defaultHandlerTimeout.Value}) is too large (max: 24 hours).");
            }
        }

        // 6. 验证默认 FailureThreshold 范围
        if (_defaultFailureThreshold.HasValue)
        {
            if (_defaultFailureThreshold.Value <= 0)
            {
                throw new InvalidOperationException(
                    $"Default FailureThreshold ({_defaultFailureThreshold.Value}) must be greater than zero.");
            }

            if (_defaultFailureThreshold.Value > 100)
            {
                throw new InvalidOperationException(
                    $"Default FailureThreshold ({_defaultFailureThreshold.Value}) is too large (max: 100).");
            }
        }

        // 7. 验证默认 AckTimeout 范围及其与 HandlerTimeout 的关系
        if (_defaultAckTimeout.HasValue)
        {
            if (_defaultAckTimeout.Value <= TimeSpan.Zero)
            {
                throw new InvalidOperationException(
                    $"Default AckTimeout ({_defaultAckTimeout.Value}) must be greater than zero.");
            }

            if (_defaultAckTimeout.Value > TimeSpan.FromHours(48))
            {
                throw new InvalidOperationException(
                    $"Default AckTimeout ({_defaultAckTimeout.Value}) is too large (max: 48 hours).");
            }

            // 如果同时设置了 HandlerTimeout，验证关系
            if (_defaultHandlerTimeout.HasValue)
            {
                if (_defaultAckTimeout.Value <= _defaultHandlerTimeout.Value)
                {
                    throw new InvalidOperationException(
                        $"Default AckTimeout ({_defaultAckTimeout.Value}) must be greater than Default HandlerTimeout ({_defaultHandlerTimeout.Value}).");
                }

                var minimumBuffer = TimeSpan.FromSeconds(1);
                if (_defaultAckTimeout.Value - _defaultHandlerTimeout.Value < minimumBuffer)
                {
                    throw new InvalidOperationException(
                        $"Default AckTimeout ({_defaultAckTimeout.Value}) should be at least {minimumBuffer} longer than Default HandlerTimeout ({_defaultHandlerTimeout.Value}).");
                }
            }
        }

        // 8. 验证事件处理器列表不包含 null
        for (var i = 0; i < _workerAddedHandlers.Count; i++)
        {
            if (_workerAddedHandlers[i] is null)
            {
                throw new InvalidOperationException($"Worker added handler at index {i} cannot be null.");
            }
        }

        for (var i = 0; i < _workerRemovedHandlers.Count; i++)
        {
            if (_workerRemovedHandlers[i] is null)
            {
                throw new InvalidOperationException($"Worker removed handler at index {i} cannot be null.");
            }
        }
    }
}

