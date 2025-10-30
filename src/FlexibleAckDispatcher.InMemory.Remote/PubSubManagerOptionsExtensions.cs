using System;
using System.Threading.Tasks;
using FlexibleAckDispatcher.Abstractions.Remote;
using FlexibleAckDispatcher.GrpcServer.NamedPipe;
using FlexibleAckDispatcher.InMemory.Core;
using FlexibleAckDispatcher.InMemory.Remote;
using Microsoft.Extensions.Logging;

namespace FlexibleAckDispatcher.InMemory.Remote;

/// <summary>
/// 远程 Worker 桥接扩展。
/// </summary>
public static class PubSubManagerOptionsExtensions
{
    private const string RemoteFeatureKey = "FlexibleAckDispatcher.RemoteBridge";

    /// <summary>
    /// 配置远程 Worker 桥接器，用于跨进程任务分发。
    /// </summary>
    public static PubSubManagerOptions WithRemoteWorkerBridge(this PubSubManagerOptions options, IRemoteWorkerBridge bridge)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(bridge);

        var feature = new RemoteBridgeFeature(bridge);
        options.SetFeature(RemoteFeatureKey, feature);

        options.RegisterInitializer(manager =>
        {
            manager.Logger.LogInformation("Remote worker bridge configured: {BridgeType}", feature.Bridge.GetType().Name);
            feature.Bridge.StartAsync(default).GetAwaiter().GetResult();
            feature.Coordinator = new RemoteWorkerCoordinator(manager.WorkerManager, feature.Bridge, manager.Logger, manager.SubscriptionDefaults);
        });

        options.RegisterDisposer(async manager =>
        {
            if (feature.Coordinator is not null)
            {
                await feature.Coordinator.DisposeAsync().ConfigureAwait(false);
            }

            await feature.Bridge.DisposeAsync().ConfigureAwait(false);
        });

        return options;
    }

    /// <summary>
    /// 配置远程 Worker 桥接器（命名管道实现）。
    /// </summary>
    public static PubSubManagerOptions WithRemoteWorkerBridge(this PubSubManagerOptions options, Action<NamedPipeRemoteWorkerOptions>? configure)
    {
        ArgumentNullException.ThrowIfNull(options);

        var bridgeOptions = new NamedPipeRemoteWorkerOptions();
        configure?.Invoke(bridgeOptions);
        return options.WithRemoteWorkerBridge(new NamedPipeRemoteWorkerBridge(bridgeOptions));
    }

    private sealed class RemoteBridgeFeature
    {
        public RemoteBridgeFeature(IRemoteWorkerBridge bridge)
        {
            Bridge = bridge;
        }

        public IRemoteWorkerBridge Bridge { get; }

        public RemoteWorkerCoordinator? Coordinator { get; set; }
    }
}
