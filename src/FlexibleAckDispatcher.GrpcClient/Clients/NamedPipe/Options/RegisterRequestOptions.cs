using System;
using System.Collections.Generic;
using System.Globalization;
using FlexibleAckDispatcher.Abstractions.Remote;
using FlexibleAckDispatcher.GrpcServer.Protos;

namespace FlexibleAckDispatcher.GrpcClient.Clients.NamedPipe.Options;

/// <summary>
/// 远程 Worker 注册请求配置。
/// </summary>
public sealed class RegisterRequestOptions
{
    /// <summary>
    /// Worker 名称，用于日志和调试识别。
    /// </summary>
    public string? WorkerName { get; private set; }

    /// <summary>
    /// 声明的最大并发数。
    /// </summary>
    public int ConcurrencyLimit { get; private set; } = 1;

    /// <summary>
    /// 预取窗口。
    /// </summary>
    public int Prefetch { get; private set; } = 1;

    /// <summary>
    /// 附加元数据。
    /// </summary>
    public IDictionary<string, string> Metadata { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 设置 Worker 名称。
    /// </summary>
    public RegisterRequestOptions WithWorkerName(string workerName)
    {
        if (string.IsNullOrWhiteSpace(workerName))
        {
            throw new ArgumentException("Worker 名称不能为空。", nameof(workerName));
        }

        WorkerName = workerName;
        return this;
    }

    /// <summary>
    /// 设置期望的最大并发度。
    /// </summary>
    public RegisterRequestOptions WithConcurrencyLimit(int limit)
    {
        if (limit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "并发度必须大于 0。");
        }

        ConcurrencyLimit = limit;
        return this;
    }

    /// <summary>
    /// 设置 Prefetch 大小。
    /// </summary>
    public RegisterRequestOptions WithPrefetch(int prefetch)
    {
        if (prefetch <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(prefetch), "Prefetch 必须大于 0。");
        }

        Prefetch = prefetch;
        return this;
    }

    /// <summary>
    /// 设置连续失败阈值（写入元数据）。
    /// </summary>
    public RegisterRequestOptions WithFailureThreshold(int threshold)
    {
        if (threshold <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(threshold), "失败阈值必须大于 0。");
        }

        Metadata[RemoteWorkerMetadataKeys.FailureThreshold] = threshold.ToString(CultureInfo.InvariantCulture);
        return this;
    }

    /// <summary>
    /// 设置单条任务的处理超时时间（写入元数据）。
    /// </summary>
    public RegisterRequestOptions WithHandlerTimeout(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "处理超时时间必须大于 0。");
        }

        Metadata[RemoteWorkerMetadataKeys.HandlerTimeoutTicks] = timeout.Ticks.ToString(CultureInfo.InvariantCulture);
        return this;
    }

    /// <summary>
    /// 自定义附加元数据。
    /// </summary>
    public RegisterRequestOptions WithMetadata(string key, string value)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("元数据键不能为空。", nameof(key));
        }

        Metadata[key] = value ?? string.Empty;
        return this;
    }

    internal RegisterRequest ToRequest()
    {
        var request = new RegisterRequest
        {
            WorkerName = WorkerName!,
            ConcurrencyLimit = ConcurrencyLimit,
            Prefetch = Prefetch
        };

        request.Metadata.Add(Metadata);
        return request;
    }

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(WorkerName))
        {
            throw new InvalidOperationException("必须设置 WorkerName。");
        }

        if (ConcurrencyLimit <= 0)
        {
            throw new InvalidOperationException("ConcurrencyLimit 必须大于 0。");
        }

        if (Prefetch <= 0)
        {
            throw new InvalidOperationException("Prefetch 必须大于 0。");
        }
    }
}

