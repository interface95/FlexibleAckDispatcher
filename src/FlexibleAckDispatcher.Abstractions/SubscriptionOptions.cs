using System;

namespace FlexibleAckDispatcher.Abstractions;

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

    /// <summary>
    /// 框架默认的 Prefetch 数量。
    /// </summary>
    public const int DefaultPrefetch = 1;

    /// <summary>
    /// 框架支持的最大并发限制。
    /// </summary>
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

    /// <summary>
    /// 获取一个带有框架默认值的订阅选项实例。
    /// </summary>
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

        EnsureConcurrencyInvariant();
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
            throw new ArgumentOutOfRangeException(nameof(limit), "Concurrency limit cannot exceed Prefetch。请先调用 WithPrefetch 调整预取数量。");
        }

        ConcurrencyLimit = limit;
        _hasCustomConcurrency = true;
        EnsureConcurrencyInvariant();
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

        if (AckTimeout.HasValue && timeout >= AckTimeout.Value)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Handler timeout must be smaller than Ack timeout.");
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

        if (timeout <= HandlerTimeout)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Ack timeout must be greater than handler timeout.");
        }
 
        AckTimeout = timeout;
        return this;
    }

    /// <summary>
    /// 快捷创建一个新的选项实例。
    /// </summary>
    public static SubscriptionOptions Create() => new(default);

    internal static SubscriptionOptions Create(SubscriptionDefaults defaults) => new(defaults);

    internal void Validate()
    {
        // 1. 验证 Prefetch 范围
        if (Prefetch <= 0)
        {
            throw new InvalidOperationException(
                $"Prefetch ({Prefetch}) must be greater than zero.");
        }

        if (Prefetch > 10000)
        {
            throw new InvalidOperationException(
                $"Prefetch ({Prefetch}) is too large (max: 10000). Large prefetch values may cause memory issues.");
        }

        // 2. 验证 ConcurrencyLimit 与 Prefetch 的关系
        if (ConcurrencyLimit > Prefetch)
        {
            throw new InvalidOperationException(
                $"ConcurrencyLimit ({ConcurrencyLimit}) cannot exceed Prefetch ({Prefetch})");
        }

        if (ConcurrencyLimit <= 0)
        {
            throw new InvalidOperationException(
                $"ConcurrencyLimit ({ConcurrencyLimit}) must be greater than zero.");
        }

        // 3. 验证 HandlerTimeout
        if (HandlerTimeout <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"HandlerTimeout ({HandlerTimeout}) must be greater than zero.");
        }

        if (HandlerTimeout > TimeSpan.FromHours(24))
        {
            throw new InvalidOperationException(
                $"HandlerTimeout ({HandlerTimeout}) is too large (max: 24 hours). Consider using a shorter timeout.");
        }

        // 4. 验证 AckTimeout 与 HandlerTimeout 的关系
        if (AckTimeout.HasValue)
        {
            if (AckTimeout.Value <= TimeSpan.Zero)
            {
                throw new InvalidOperationException(
                    $"AckTimeout ({AckTimeout.Value}) must be greater than zero.");
            }

            if (AckTimeout.Value > TimeSpan.FromHours(48))
            {
                throw new InvalidOperationException(
                    $"AckTimeout ({AckTimeout.Value}) is too large (max: 48 hours).");
            }

            if (HandlerTimeout >= AckTimeout.Value)
            {
                throw new InvalidOperationException(
                    $"HandlerTimeout ({HandlerTimeout}) must be less than AckTimeout ({AckTimeout.Value})");
            }

            // AckTimeout 应该给 HandlerTimeout 留出足够的缓冲时间
            var minimumBuffer = TimeSpan.FromSeconds(1);
            if (AckTimeout.Value - HandlerTimeout < minimumBuffer)
            {
                throw new InvalidOperationException(
                    $"AckTimeout ({AckTimeout.Value}) should be at least {minimumBuffer} longer than HandlerTimeout ({HandlerTimeout}) to allow proper cleanup.");
            }
        }

        // 5. 验证 FailureThreshold
        if (FailureThreshold <= 0)
        {
            throw new InvalidOperationException(
                $"FailureThreshold ({FailureThreshold}) must be greater than zero.");
        }

        if (FailureThreshold > 100)
        {
            throw new InvalidOperationException(
                $"FailureThreshold ({FailureThreshold}) is too large (max: 100). Consider using a lower threshold.");
        }

        // 6. 验证 Name（如果设置）
        if (Name != null && string.IsNullOrWhiteSpace(Name))
        {
            throw new InvalidOperationException(
                "Name cannot be empty or whitespace. Use null if you don't want to set a name.");
        }
    }

    private void EnsureConcurrencyInvariant()
    {
        if (ConcurrencyLimit > Prefetch)
        {
            throw new InvalidOperationException("Concurrency limit must be less than or equal to Prefetch.");
        }
    }
}

internal readonly struct SubscriptionDefaults
{
    public int? Prefetch { get; init; }
    public int? ConcurrencyLimit { get; init; }
    public TimeSpan? HandlerTimeout { get; init; }
    public int? FailureThreshold { get; init; }
    public TimeSpan? AckTimeout { get; init; }
}
