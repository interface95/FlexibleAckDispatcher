using System;

namespace FlexibleAckDispatcher.GrpcClient.Clients.NamedPipe.Options;

/// <summary>
/// 配置 <see cref="NamedPipeRemoteWorkerClient"/> 的基础选项。
/// </summary>
public sealed class NamedPipeRemoteWorkerClientOptions
{
    private bool _autoHeartbeatEnabled;
    private TimeSpan _heartbeatInterval = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 命名管道名称，必须与服务端保持一致。
    /// </summary>
    public string PipeName { get; private set; } = "FlexibleAckDispatcher";

    /// <summary>
    /// gRPC 服务端名称，通常为 "." 表示本地。
    /// </summary>
    public string ServerName { get; private set; } = ".";

    /// <summary>
    /// 是否启用自动心跳。
    /// </summary>
    public bool AutoHeartbeatEnabled => _autoHeartbeatEnabled;

    /// <summary>
    /// 自动心跳的发送间隔。
    /// </summary>
    public TimeSpan HeartbeatInterval => _heartbeatInterval;

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
    /// 启用自动心跳，并可选指定心跳间隔。
    /// </summary>
    public NamedPipeRemoteWorkerClientOptions WithAutoHeartbeat(TimeSpan? interval = null)
    {
        _autoHeartbeatEnabled = true;
        if (interval.HasValue)
        {
            if (interval.Value <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(interval), "心跳间隔必须大于 0。");
            }

            _heartbeatInterval = interval.Value;
        }

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

        if (_autoHeartbeatEnabled && _heartbeatInterval <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("Heartbeat interval must be greater than zero.");
        }
    }
}

