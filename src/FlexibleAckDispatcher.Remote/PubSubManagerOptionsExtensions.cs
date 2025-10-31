using System;
using FlexibleAckDispatcher.Abstractions.Remote;
using FlexibleAckDispatcher.GrpcServer.NamedPipe;
using FlexibleAckDispatcher.InMemory;
using FlexibleAckDispatcher.InMemory.Modules;
using FlexibleAckDispatcher.Remote.Modules;

namespace FlexibleAckDispatcher.Remote;

/// <summary>
/// 远程 Worker 桥接扩展。
/// </summary>
public static class PubSubManagerOptionsExtensions
{
    /// <summary>
    /// 配置远程 Worker 桥接器，用于跨进程任务分发。
    /// </summary>
    public static PubSubManagerOptions WithRemoteWorkerBridge(this PubSubManagerOptions options, IRemoteWorkerBridge bridge)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(bridge);

        return options.AddModule(new RemoteBridgeModule(bridge));
    }

    /// <summary>
    /// 通过工厂配置远程 Worker 桥接器，用于跨进程任务分发。
    /// </summary>
    public static PubSubManagerOptions WithRemoteWorkerBridge(this PubSubManagerOptions options, Func<IRemoteWorkerBridge> bridgeFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(bridgeFactory);

        return options.AddModule(new RemoteBridgeModule(bridgeFactory));
    }

    /// <summary>
    /// 配置命名管道实现的远程 Worker 桥接器。
    /// </summary>
    public static PubSubManagerOptions WithRemoteWorkerBridge(this PubSubManagerOptions options, Action<NamedPipeRemoteWorkerOptions>? configure)
    {
        ArgumentNullException.ThrowIfNull(options);

        return options.AddModule(new NamedPipeRemoteModule(configure));
    }

    /// <summary>
    /// 使用模块化方式注册命名管道远程桥。
    /// </summary>
    public static PubSubManagerOptions UseNamedPipeRemote(this PubSubManagerOptions options, Action<NamedPipeRemoteWorkerOptions>? configure = null)
        {
        return options.WithRemoteWorkerBridge(configure);
    }
}
