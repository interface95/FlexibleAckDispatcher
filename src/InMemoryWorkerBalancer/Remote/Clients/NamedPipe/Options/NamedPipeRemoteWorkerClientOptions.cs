using System;

namespace InMemoryWorkerBalancer.Remote.Clients.NamedPipe.Options;

/// <summary>
/// 配置 <see cref="NamedPipeRemoteWorkerClient"/> 的基础选项。
/// </summary>
public sealed class NamedPipeRemoteWorkerClientOptions
{
    /// <summary>
    /// 命名管道名称，必须与服务端保持一致。
    /// </summary>
    public string PipeName { get; private set; } = "FlexibleAckDispatcher";

    /// <summary>
    /// gRPC 服务端名称，通常为 "." 表示本地。
    /// </summary>
    public string ServerName { get; private set; } = ".";

    /// <summary>
    /// 设置命名管道名称。
    /// </summary>
    public NamedPipeRemoteWorkerClientOptions WithPipeName(string pipeName)
    {
        if (string.IsNullOrWhiteSpace(pipeName))
        {
            throw new ArgumentException("管道名称不能为空。", nameof(pipeName));
        }

        PipeName = pipeName;
        return this;
    }

    /// <summary>
    /// 设置 gRPC 服务端名称。
    /// </summary>
    public NamedPipeRemoteWorkerClientOptions WithServerName(string serverName)
    {
        if (string.IsNullOrWhiteSpace(serverName))
        {
            throw new ArgumentException("服务端名称不能为空。", nameof(serverName));
        }

        ServerName = serverName;
        return this;
    }

    /// <summary>
    /// 验证配置。
    /// </summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(PipeName))
        {
            throw new InvalidOperationException("PipeName must be provided.");
        }

        if (string.IsNullOrWhiteSpace(ServerName))
        {
            throw new InvalidOperationException("ServerName must be provided.");
        }
    }
}

