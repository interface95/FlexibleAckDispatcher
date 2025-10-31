using System;
using FlexibleAckDispatcher.Abstractions.Remote;
using FlexibleAckDispatcher.InMemory;
using FlexibleAckDispatcher.InMemory.Modules;

namespace FlexibleAckDispatcher.Remote.Modules;

internal sealed class RemoteBridgeModule : IPubSubManagerModule
{
    private readonly Func<IRemoteWorkerBridge>? _bridgeFactory;
    private readonly IRemoteWorkerBridge? _bridgeInstance;

    public RemoteBridgeModule(IRemoteWorkerBridge bridge)
    {
        _bridgeInstance = bridge ?? throw new ArgumentNullException(nameof(bridge));
    }

    public RemoteBridgeModule(Func<IRemoteWorkerBridge> bridgeFactory)
    {
        _bridgeFactory = bridgeFactory ?? throw new ArgumentNullException(nameof(bridgeFactory));
    }

    public void Configure(PubSubManagerOptions options)
    {
        var bridge = _bridgeInstance ?? _bridgeFactory?.Invoke();
        if (bridge is null)
        {
            throw new InvalidOperationException("Remote bridge factory returned null instance.");
        }

        RemoteBridgeConfigurator.Attach(options, bridge);
    }
}
