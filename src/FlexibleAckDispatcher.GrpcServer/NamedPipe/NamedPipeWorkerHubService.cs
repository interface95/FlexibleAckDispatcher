using System.Threading;
using System.Threading.Tasks;
using FlexibleAckDispatcher.GrpcServer.Protos;
using Grpc.Core;

namespace FlexibleAckDispatcher.GrpcServer.NamedPipe;

/// <summary>
/// gRPC 服务端实现，负责将消息转交给 <see cref="NamedPipeRemoteWorkerBridge"/>。
/// </summary>
internal sealed class NamedPipeWorkerHubService : WorkerHub.WorkerHubBase
{
    private readonly NamedPipeRemoteWorkerBridge _bridge;

    public NamedPipeWorkerHubService(NamedPipeRemoteWorkerBridge bridge)
    {
        _bridge = bridge;
    }

    public override Task<RegisterReply> Register(RegisterRequest request, ServerCallContext context)
    {
        return _bridge.HandleRegisterAsync(request, context.CancellationToken);
    }

    public override Task Subscribe(SubscribeRequest request, IServerStreamWriter<DispatcherMessage> responseStream, ServerCallContext context)
    {
        return _bridge.HandleSubscribeAsync(request.WorkerId, responseStream, context.CancellationToken);
    }

    public override async Task<AckReply> Ack(AckRequest request, ServerCallContext context)
    {
        await _bridge.HandleAckAsync(request.WorkerId, request.DeliveryTag).ConfigureAwait(false);
        return new AckReply();
    }

    public override async Task<AckReply> Nack(NackRequest request, ServerCallContext context)
    {
        await _bridge.HandleNackAsync(request.WorkerId, request.DeliveryTag, request.Requeue, request.Reason).ConfigureAwait(false);
        return new AckReply();
    }

    public override async Task<HeartbeatReply> Heartbeat(HeartbeatRequest request, ServerCallContext context)
    {
        await _bridge.HandleHeartbeatAsync(request.WorkerId).ConfigureAwait(false);
        return new HeartbeatReply();
    }
}
