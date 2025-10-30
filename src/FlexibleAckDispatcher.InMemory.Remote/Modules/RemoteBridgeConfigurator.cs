using System;
using System.Threading;
using FlexibleAckDispatcher.Abstractions.Remote;
using FlexibleAckDispatcher.InMemory.Core;
using FlexibleAckDispatcher.InMemory.Core.Modules;
using Microsoft.Extensions.Logging;

namespace FlexibleAckDispatcher.InMemory.Remote.Modules;

internal static class RemoteBridgeConfigurator
{
    private const string RemoteFeatureKey = "FlexibleAckDispatcher.RemoteBridge";

    internal static void Attach(PubSubManagerOptions options, IRemoteWorkerBridge bridge)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(bridge);

        var feature = new RemoteBridgeFeature(bridge);
        options.SetFeature(RemoteFeatureKey, feature);

        options.RegisterInitializer(manager =>
        {
            manager.Logger.LogInformation("Remote worker bridge configured: {BridgeType}", bridge.GetType().Name);
            bridge.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            feature.Coordinator = new RemoteWorkerCoordinator(manager.WorkerManager, bridge, manager.Logger, manager.SubscriptionDefaults);
        });

        options.RegisterDisposer(async _ =>
        {
            if (feature.Coordinator is not null)
            {
                await feature.Coordinator.DisposeAsync().ConfigureAwait(false);
            }

            await bridge.DisposeAsync().ConfigureAwait(false);
        });
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
