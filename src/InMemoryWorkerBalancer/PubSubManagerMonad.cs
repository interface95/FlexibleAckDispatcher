using System;
using InMemoryWorkerBalancer.Abstractions;
using Microsoft.Extensions.Logging;

namespace InMemoryWorkerBalancer;

/// <summary>
/// 使用 Fluent Interface 模式构建 <see cref="PubSubManager"/> 的构建器。
/// 提供更简洁的链式调用 API。
/// </summary>
public sealed class PubSubManagerMonad
{
    private readonly PubSubManagerOptions _options = new();

    private PubSubManagerMonad()
    {
    }

    /// <summary>
    /// 创建一个默认的构建器实例。
    /// </summary>
    public static PubSubManagerMonad Builder()
    {
        return new PubSubManagerMonad();
    }

    /// <summary>
    /// 配置序列化器。
    /// </summary>
    public PubSubManagerMonad Serializer(IWorkerPayloadSerializer serializer)
    {
        _options.WithSerializer(serializer);
        return this;
    }

    /// <summary>
    /// 配置日志记录器。
    /// </summary>
    public PubSubManagerMonad Logger(ILogger logger)
    {
        _options.WithLogger(logger);
        return this;
    }

    /// <summary>
    /// 配置 Worker 选择策略。
    /// </summary>
    public PubSubManagerMonad SelectionStrategy(Func<IWorkerSelectionStrategy> strategyFactory)
    {
        _options.WithSelectionStrategy(strategyFactory);
        return this;
    }

    /// <summary>
    /// 配置 ACK 监控间隔。
    /// </summary>
    public PubSubManagerMonad AckMonitor(TimeSpan interval)
    {
        _options.WithAckMonitorInterval(interval);
        return this;
    }

    /// <summary>
    /// 配置默认的 Prefetch 数量。
    /// </summary>
    public PubSubManagerMonad Prefetch(int prefetch)
    {
        _options.WithDefaultPrefetch(prefetch);
        return this;
    }

    /// <summary>
    /// 配置默认的并发限制。
    /// </summary>
    public PubSubManagerMonad Concurrency(int limit)
    {
        _options.WithDefaultConcurrencyLimit(limit);
        return this;
    }

    /// <summary>
    /// 配置默认的处理超时时间。
    /// </summary>
    public PubSubManagerMonad HandlerTimeout(TimeSpan timeout)
    {
        _options.WithDefaultHandlerTimeout(timeout);
        return this;
    }

    /// <summary>
    /// 配置默认的失败阈值。
    /// </summary>
    public PubSubManagerMonad FailureThreshold(int threshold)
    {
        _options.WithDefaultFailureThreshold(threshold);
        return this;
    }

    /// <summary>
    /// 配置默认的 ACK 超时时间。
    /// </summary>
    public PubSubManagerMonad AckTimeout(TimeSpan timeout)
    {
        _options.WithDefaultAckTimeout(timeout);
        return this;
    }

    /// <summary>
    /// 注册 Worker 添加事件处理器。
    /// </summary>
    public PubSubManagerMonad OnWorkerAdded(Func<WorkerEndpointSnapshot, Task> handler)
    {
        _options.OnWorkerAddedHandler(handler);
        return this;
    }

    /// <summary>
    /// 注册 Worker 移除事件处理器。
    /// </summary>
    public PubSubManagerMonad OnWorkerRemoved(Func<WorkerEndpointSnapshot, Task> handler)
    {
        _options.OnWorkerRemovedHandler(handler);
        return this;
    }

    /// <summary>
    /// 构建并返回配置好的 <see cref="PubSubManager"/> 实例。
    /// </summary>
    public PubSubManager Create()
    {
        // 验证配置
        _options.Validate();
        
        // 使用内部构造函数创建 Manager
        return new PubSubManager(_options);
    }
}

