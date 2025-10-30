using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using InMemoryWorkerBalancer;
using InMemoryWorkerBalancer.Remote;
using InMemoryWorkerBalancer.Remote.Clients.NamedPipe;
using InMemoryWorkerBalancer.Remote.NamedPipe;
using InMemoryWorkerBalancer.Remote.Protos;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Extensions.Logging.Abstractions;

namespace TestProject2;

[TestClass]
public sealed class TestNamedPipeRemoteBridge
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [TestMethod]
    public async Task NamedPipeBridge_ShouldDispatchAndAck()
    {
        var pipeName = $"test-pipe-{Guid.NewGuid():N}";
        var options = new NamedPipeRemoteWorkerOptions
        {
            PipeName = pipeName,
            MaxConcurrentSessions = 4
        };

        await using var bridge = new NamedPipeRemoteWorkerBridge(options);

        var sessionTcs =
            new TaskCompletionSource<RemoteWorkerSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ackTcs =
            new TaskCompletionSource<RemoteTaskAcknowledgement>(TaskCreationOptions.RunContinuationsAsynchronously);

        bridge.WorkerConnected += session =>
        {
            sessionTcs.TrySetResult(session);
            return ValueTask.CompletedTask;
        };

        bridge.TaskAcknowledged += acknowledgement =>
        {
            ackTcs.TrySetResult(acknowledgement);
            return ValueTask.CompletedTask;
        };

        await bridge.StartAsync();

        await using var client = NamedPipeRemoteWorkerClient.Create(o => o.WithPipeName(pipeName));

        var registerReply = await client.RegisterAsync(options =>
        {
            options
                .WithWorkerName("TestWorker")
                .WithConcurrencyLimit(1)
                .WithPrefetch(1);
        }).WaitAsync(TestTimeout);
        var workerId = registerReply.WorkerId;

        var session = await sessionTcs.Task.WaitAsync(TestTimeout);

        using var subscription = client.SubscribeAsync(options => options.WithWorkerId(workerId));

        using var subscriptionCts = new CancellationTokenSource(TestTimeout);
        var moveNextTask = subscription.ResponseStream.MoveNext(subscriptionCts.Token);

        var payload = Encoding.UTF8.GetBytes("hello");
        var envelope = new RemoteTaskEnvelope(42, "text/plain", payload, null);
        await bridge.DispatchAsync(new RemoteTaskDispatch(session, envelope, CancellationToken.None));

        Assert.IsTrue(await moveNextTask, "未接收到远程任务。");
        var message = subscription.ResponseStream.Current;
        Assert.IsNotNull(message.Task, "返回的消息缺少 Task 有效载荷。");
        Assert.AreEqual(envelope.DeliveryTag, message.Task.DeliveryTag);
        Assert.AreEqual(envelope.MessageType, message.Task.MessageType);
        CollectionAssert.AreEqual(payload, message.Task.Payload.ToByteArray());

        await client.AckAsync(options =>
        {
            options.WithWorkerId(workerId)
                    .WithDeliveryTag(message.Task.DeliveryTag);
        }).WaitAsync(TestTimeout);

        var acknowledgement = await ackTcs.Task.WaitAsync(TestTimeout);
        Assert.AreEqual(workerId, acknowledgement.WorkerId);
        Assert.AreEqual(message.Task.DeliveryTag, acknowledgement.DeliveryTag);

        await bridge.StopAsync();
    }

    [TestMethod]
    public async Task PubSubManager_ShouldDispatchToRemoteWorker()
    {
        var pipeName = $"test-pipe-{Guid.NewGuid():N}";
        await using var manager = PubSubManager.Create(opts =>
        {
            opts.WithSerializer(JsonWorkerPayloadSerializer.Default)
                .WithLogger(NullLogger.Instance)
                .WithRemoteWorkerBridge(options =>
                {
                    options.PipeName = pipeName;
                    options.MaxConcurrentSessions = 4;
                });
        });

        await using var client = NamedPipeRemoteWorkerClient.Create(o => o.WithPipeName(pipeName));

        var registerReply = await client.RegisterAsync(options =>
        {
            options
                .WithWorkerName("RemoteSubscriber")
                .WithConcurrencyLimit(1)
                .WithPrefetch(1)
                .WithMetadata(RemoteWorkerMetadataKeys.MessageType,
                    typeof(string).FullName ?? "System.String");
        }).WaitAsync(TestTimeout);

        var workerId = registerReply.WorkerId;

        using var subscription = client.SubscribeAsync(options => options.WithWorkerId(workerId));
        using var cts = new CancellationTokenSource(TestTimeout);
        var moveNextTask = subscription.ResponseStream.MoveNext(cts.Token);

        await manager.PublishAsync("hello-remote");

        Assert.IsTrue(await moveNextTask, "远程 worker 未收到任务");
        var message = subscription.ResponseStream.Current;
        Assert.IsNotNull(message.Task);
        var payloadText = Encoding.UTF8.GetString(message.Task.Payload.ToByteArray());
        StringAssert.Contains(payloadText, "hello-remote");

        await client.AckAsync(options =>
        {
            options
                .WithWorkerId(workerId)
                .WithDeliveryTag(message.Task.DeliveryTag);
        }).WaitAsync(TestTimeout);

        Assert.IsTrue(SpinWait.SpinUntil(() => manager.CompletedCount >= 1, TestTimeout), "远程 Ack 未在期望时间内生效");
    }
}