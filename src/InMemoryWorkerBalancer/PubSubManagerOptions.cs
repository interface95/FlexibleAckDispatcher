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

    internal IWorkerPayloadSerializer Serializer => _serializer;

    internal ILogger Logger => _logger;

    internal IReadOnlyList<Func<WorkerEndpointSnapshot, Task>> WorkerAddedHandlers => _workerAddedHandlers;

    internal IReadOnlyList<Func<WorkerEndpointSnapshot, Task>> WorkerRemovedHandlers => _workerRemovedHandlers;

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

