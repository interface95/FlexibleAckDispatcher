using System;

namespace InMemoryWorkerBalancer;

/// <summary>
/// 订阅选项，支持以构建者模式配置各项参数。
/// </summary>
public sealed class SubscriptionOptions
{
    private bool _hasCustomConcurrency;

    private SubscriptionOptions(SubscriptionDefaults defaults)
    {
        Prefetch = DefaultPrefetch;
        ConcurrencyLimit = DefaultPrefetch;
        HandlerTimeout = DefaultTimeout;
        FailureThreshold = 3;
        AckTimeout = null;

        if (defaults.Prefetch.HasValue)
        {
            WithPrefetch(defaults.Prefetch.Value);
        }

        if (defaults.ConcurrencyLimit.HasValue)
        {
            WithConcurrencyLimit(defaults.ConcurrencyLimit.Value);
        }

        if (defaults.HandlerTimeout.HasValue)
        {
            WithHandlerTimeout(defaults.HandlerTimeout.Value);
        }

        if (defaults.FailureThreshold.HasValue)
        {
            WithFailureThreshold(defaults.FailureThreshold.Value);
        }

        if (defaults.AckTimeout.HasValue)
        {
            WithAckTimeout(defaults.AckTimeout.Value);
        }
    }

    public const int DefaultPrefetch = 1;
    public const int MaxConcurrencyLimit = 50;
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// 每个订阅默认的 Prefetch 数量。
    /// </summary>
    public int Prefetch { get; private set; } = DefaultPrefetch;
    /// <summary>
    /// 订阅别名，方便日志排查。
    /// </summary>
    public string? Name { get; private set; }
    /// <summary>
    /// Worker 内部允许的最大并发数。
    /// </summary>
    public int ConcurrencyLimit { get; private set; } = DefaultPrefetch;
    /// <summary>
    /// Handler 执行的超时时间。
    /// </summary>
    public TimeSpan HandlerTimeout { get; private set; } = DefaultTimeout;
    /// <summary>
    /// 连续失败或超时的阈值。
    /// </summary>
    public int FailureThreshold { get; private set; } = 3;
    /// <summary>
    /// ACK 超时时间，超过该时间未确认的消息将被自动释放。
    /// </summary>
    public TimeSpan? AckTimeout { get; private set; }

    public static SubscriptionOptions Defaults { get; } = new(default);

    /// <summary>
    /// 设置 Prefetch，影响每次最多预取的消息数。
    /// </summary>
    public SubscriptionOptions WithPrefetch(int prefetch)
    {
        if (prefetch <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(prefetch), "Prefetch must be greater than zero.");
        }

        Prefetch = prefetch;

        if (!_hasCustomConcurrency)
        {
            ConcurrencyLimit = Math.Min(prefetch, MaxConcurrencyLimit);
        }
        else if (ConcurrencyLimit > Prefetch)
        {
            ConcurrencyLimit = Prefetch;
        }

        return this;
    }

    /// <summary>
    /// 设置订阅友好名称。
    /// </summary>
    public SubscriptionOptions WithName(string name)
    {
        Name = name;
        return this;
    }

    /// <summary>
    /// 设置 Worker 内部的最大并发限制。
    /// </summary>
    public SubscriptionOptions WithConcurrencyLimit(int limit)
    {
        switch (limit)
        {
            // 更严格的上限
            case <= 0 or > 100:
                throw new ArgumentOutOfRangeException(nameof(limit), 
                    "Concurrency limit must be between 1 and 100.");
            case > MaxConcurrencyLimit:
                throw new ArgumentOutOfRangeException(nameof(limit), $"Concurrency limit cannot exceed {MaxConcurrencyLimit}.");
        }

        if (limit > Prefetch)
        {
            Prefetch = limit;
        }

        ConcurrencyLimit = limit;
        _hasCustomConcurrency = true;
        return this;
    }

    /// <summary>
    /// 设置单条消息处理的超时时间。
    /// </summary>
    public SubscriptionOptions WithHandlerTimeout(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Handler timeout must be greater than zero.");
        }

        HandlerTimeout = timeout;
        return this;
    }

    /// <summary>
    /// 设置连续失败或超时的阈值。
    /// </summary>
    public SubscriptionOptions WithFailureThreshold(int threshold)
    {
        if (threshold <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(threshold), "Failure threshold must be greater than zero.");
        }

        FailureThreshold = threshold;
        return this;
    }

    /// <summary>
    /// 设置 ACK 超时时间，超过该时间未确认的消息将被自动释放。
    /// </summary>
    public SubscriptionOptions WithAckTimeout(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Ack timeout must be greater than zero.");
        }

        AckTimeout = timeout;
        return this;
    }

    /// <summary>
    /// 快捷创建一个新的选项实例。
    /// </summary>
    public static SubscriptionOptions Create() => new(default);

    internal static SubscriptionOptions Create(SubscriptionDefaults defaults) => new(defaults);
}

internal readonly struct SubscriptionDefaults
{
    public int? Prefetch { get; init; }
    public int? ConcurrencyLimit { get; init; }
    public TimeSpan? HandlerTimeout { get; init; }
    public int? FailureThreshold { get; init; }
    public TimeSpan? AckTimeout { get; init; }
}
