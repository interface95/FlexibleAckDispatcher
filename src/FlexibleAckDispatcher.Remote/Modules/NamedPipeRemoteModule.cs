using System;
using FlexibleAckDispatcher.InMemory;
using FlexibleAckDispatcher.InMemory.Modules;
using FlexibleAckDispatcher.GrpcServer.NamedPipe;

namespace FlexibleAckDispatcher.Remote.Modules;

/// <summary>
/// 提供基于命名管道的远程桥接模块。
/// </summary>
public sealed class NamedPipeRemoteModule : IPubSubManagerModule
{
    private readonly Action<NamedPipeRemoteWorkerOptions>? _configure;

    /// <summary>
    /// 初始化 <see cref="NamedPipeRemoteModule"/>。
    /// </summary>
    /// <param name="configure">命名管道桥接器的附加配置。</param>
    public NamedPipeRemoteModule(Action<NamedPipeRemoteWorkerOptions>? configure = null)
    {
        _configure = configure;
    }

    /// <inheritdoc />
    public void Configure(PubSubManagerOptions options)
    {
        var bridgeOptions = new NamedPipeRemoteWorkerOptions();
        _configure?.Invoke(bridgeOptions);
        var bridge = new NamedPipeRemoteWorkerBridge(bridgeOptions);
        RemoteBridgeConfigurator.Attach(options, bridge);
    }
}
