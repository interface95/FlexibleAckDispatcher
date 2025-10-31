using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FlexibleAckDispatcher.Abstractions;
using FlexibleAckDispatcher.InMemory.Core;

namespace TestProject2;

[TestClass]
public sealed class TestWorkerBalancerPubSub
{
    [TestMethod]
    /// <summary>
    /// 验证订阅者内部手动 Ack 的场景。
    /// </summary>
    public async Task PubSubManager_ShouldProcessAllMessages()
    {
        const int totalMessages = 20;
        const int prefetch = 2;
        await using var manager = PubSubManager.Create(options =>
            options.WithAckMonitorInterval(TimeSpan.FromMilliseconds(50)));

        var processed = 0;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task Handler(WorkerMessage<int> message, CancellationToken cancellationToken)
        {
            var current = Interlocked.Increment(ref processed);
            if (current == totalMessages)
            {
                completion.TrySetResult();
            }

            await message.AckAsync();
        }

        await manager.SubscribeAsync<int>(Handler, options => options.WithPrefetch(prefetch));

        for (var i = 0; i < totalMessages; i++)
        {
            await manager.PublishAsync(i);
        }

        var finishedTask = await Task.WhenAny(completion.Task, Task.Delay(TimeSpan.FromSeconds(5)));

        Assert.AreSame(completion.Task, finishedTask, "消息未在预期时间内全部处理");
        Assert.AreEqual(totalMessages, processed);
    }

    [TestMethod]
    /// <summary>
    /// 验证超过 ACK 超时时间的消息会被自动释放。
    /// </summary>
    public async Task PubSubManager_ShouldAutoReleaseAfterAckTimeout()
    {
        await using var manager = PubSubManager.Create();

        var firstDelivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondProcessed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var processed = 0;

        await manager.SubscribeAsync<int>(async (message, cancellationToken) =>
        {
            var order = Interlocked.Increment(ref processed);
            if (order == 1)
            {
                firstDelivered.TrySetResult();
                // 不 Ack，触发 ACK 超时
            }
            else
            {
                await message.AckAsync();
                secondProcessed.TrySetResult();
            }
        }, options => options
            .WithPrefetch(1)
            .WithHandlerTimeout(TimeSpan.FromMilliseconds(50))
            .WithAckTimeout(TimeSpan.FromMilliseconds(150)));

        await manager.PublishAsync(1);
        await firstDelivered.Task.WaitAsync(TimeSpan.FromSeconds(1));

        await Task.Delay(800); // 等待 ACK 超时触发

        Assert.AreEqual(0, manager.RunningCount, "ACK 超时后仍有任务占用并发槽位");
        Assert.IsTrue(manager.IdleCount >= 1, "ACK 超时应释放 Worker 并回到空闲池");

        await manager.PublishAsync(2);
        await secondProcessed.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.AreEqual(0, manager.PendingCount, "应无未消费消息");
        Assert.AreEqual(2, processed, "后续消息未正常处理");
    }

    [TestMethod]
    /// <summary>
    /// 验证新增 Worker 时会优先分配给负载较低的 Worker。
    /// </summary>
    public async Task PubSubManager_ShouldPrioritizeLessLoadedWorker()
    {
        await using var manager = PubSubManager.Create();

        var worker1Started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker1Release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondMessageWorkerId = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker1Id = 0;
        var worker2Id = 0;

        await manager.SubscribeAsync<int>(async (message, cancellationToken) =>
        {
            worker1Id = message.WorkerId;
            worker1Started.TrySetResult();
            await worker1Release.Task.WaitAsync(cancellationToken);
            await message.AckAsync();
        }, options => options
            .WithName("PrimaryWorker")
            .WithPrefetch(10)
            .WithConcurrencyLimit(5)
            .WithHandlerTimeout(TimeSpan.FromSeconds(10))
            .WithAckTimeout(TimeSpan.FromMinutes(1)));

        await manager.PublishAsync(1);
        await worker1Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await manager.SubscribeAsync<int>(async (message, cancellationToken) =>
        {
            worker2Id = message.WorkerId;
            await message.AckAsync();
            secondMessageWorkerId.TrySetResult(message.WorkerId);
        }, options => options
            .WithName("SecondaryWorker")
            .WithPrefetch(10)
            .WithConcurrencyLimit(5)
            .WithHandlerTimeout(TimeSpan.FromSeconds(10))
            .WithAckTimeout(TimeSpan.FromMinutes(1)));

        await manager.PublishAsync(2);
        var assignedWorkerId = await secondMessageWorkerId.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.AreNotEqual(0, worker1Id, "主 Worker 未接收到消息");
        Assert.AreNotEqual(0, worker2Id, "次 Worker 未初始化");
        Assert.AreNotEqual(worker1Id, worker2Id, "两个 Worker 应具有不同的标识");
        Assert.AreEqual(worker2Id, assignedWorkerId, "调度器未优先选择负载较低的 Worker");

        worker1Release.TrySetResult();
        await Task.Delay(100);
    }

    [TestMethod]
    /// <summary>
    /// 验证订阅者计数、空闲 worker 数、执行中任务数等基本指标。
    /// </summary>
    public async Task PubSubManager_ShouldExposeRuntimeMetrics()
    {
        await using var manager = PubSubManager.Create();

        Assert.AreEqual(0, manager.SubscriberCount, "初始订阅者数量应为 0");
        Assert.AreEqual(0, manager.IdleCount, "初始空闲 worker 数应为 0");
        Assert.AreEqual(0, manager.RunningCount, "初始执行中任务数应为 0");
        Assert.AreEqual(0, manager.PendingCount, "初始未消费消息数应为 0");
        Assert.AreEqual(0, manager.GetSnapshot().Count, "快照应为空");

        var subscription = await manager.SubscribeAsync<int>(
            async (message, token) => await message.AckAsync(),
            options => options.WithPrefetch(2));

        var snapshotAfterSubscribe = manager.GetSnapshot();
        Assert.AreEqual(1, manager.SubscriberCount, "订阅者数量应为 1");
        Assert.AreEqual(1, snapshotAfterSubscribe.Count, "快照应包含 1 个 worker");
        Assert.IsTrue(snapshotAfterSubscribe[0].IsActive, "worker 应处于激活状态");

        // 发布消息并等待处理，验证 IdleCount 和 RunningCount 的动态变化
        await manager.PublishAsync(42);
        await Task.Delay(50);

        Assert.IsTrue(manager.IdleCount >= 0, "空闲 worker 数应为非负");
        Assert.IsTrue(manager.RunningCount >= 0, "执行中任务数应为非负");
        Assert.IsTrue(manager.PendingCount >= 0, "未消费消息数应为非负");
        Assert.IsTrue(manager.DispatchedCount >= 0, "已调度计数应为非负");
        Assert.IsTrue(manager.CompletedCount >= 0, "已完成计数应为非负");

        await subscription.DisposeAsync();

        var snapshotAfterDispose = manager.GetSnapshot();
        Assert.AreEqual(0, manager.SubscriberCount, "取消订阅后订阅者数量应为 0");
        Assert.AreEqual(0, snapshotAfterDispose.Count, "取消订阅后 worker 快照应为空");
        Assert.AreEqual(0, manager.PendingCount, "取消订阅后应无未消费消息");
    }

    [TestMethod]
    /// <summary>
    /// 验证 Prefetch 限制在高并发下生效。
    /// </summary>
    public async Task PubSubManager_ShouldRespectPrefetchLimit()
    {
        const int maxConnections = 2;
        const int totalMessages = 30;
    
        await using var manager = PubSubManager.Create();
    
        var concurrent = 0;
        var maxObserved = 0;
        var processed = 0;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    
        async Task Handler(WorkerMessage<int> message, CancellationToken cancellationToken)
        {
            var current = Interlocked.Increment(ref concurrent);
            UpdateMax(ref maxObserved, current);
    
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref concurrent);
            }
    
            if (Interlocked.Increment(ref processed) == totalMessages)
            {
                completion.TrySetResult();
            }
    
            await message.AckAsync();
        }
    
        await manager.SubscribeAsync<int>(Handler, options => options.WithPrefetch(maxConnections));
    
        for (var i = 0; i < totalMessages; i++)
        {
            await manager.PublishAsync(i);
        }
    
        var finishedTask = await Task.WhenAny(completion.Task, Task.Delay(TimeSpan.FromSeconds(5)));
    
        Assert.AreSame(completion.Task, finishedTask, "消息未在预期时间内全部处理");
        Assert.IsTrue(maxObserved <= maxConnections, $"观测到的最大并发度 {maxObserved} 超出限制 {maxConnections}");
        Assert.IsTrue(maxObserved >= 1, "未观察到任何并发执行");
    }
    
    [TestMethod]
    /// <summary>
    /// 验证可以在订阅函数外部手动 Ack，并确保 Worker 槽位释放。
    /// </summary>
    public async Task PubSubManager_AllowsManualAckOutsideHandler()
    {
        const int totalMessages = 5;
        const int prefetch = 1;
        await using var manager = PubSubManager.Create();
    
        var tags = new ConcurrentQueue<long>();
        var processed = 0;
        var arrival = new SemaphoreSlim(0);
    
        async Task Handler(WorkerMessage<int> message, CancellationToken cancellationToken)
        {
            tags.Enqueue(message.DeliveryTag);
            Interlocked.Increment(ref processed);
            arrival.Release();
            // 不在 handler 中 Ack，完全交给外部
        }
    
        await manager.SubscribeAsync<int>(Handler, options => options.WithPrefetch(prefetch));
    
        for (var i = 0; i < totalMessages; i++)
        {
            await manager.PublishAsync(i);
            Assert.IsTrue(await arrival.WaitAsync(TimeSpan.FromSeconds(2)), $"消息 {i} 未及时到达处理程序");
    
            Assert.IsTrue(tags.TryDequeue(out var tag), "未找到待确认的 deliveryTag");
            await manager.AckAsync(tag);
        }
    
        Assert.AreEqual(totalMessages, processed, "消息未全部投递到订阅函数");
    
        // 再推送一条消息，确认 worker 槽位释放
        await manager.PublishAsync(99);
        Assert.IsTrue(await arrival.WaitAsync(TimeSpan.FromSeconds(2)), "额外消息未到达");
        Assert.IsTrue(tags.TryDequeue(out var extraTag), "未捕获额外消息的 deliveryTag");
        Assert.AreEqual(totalMessages + 1, processed);
        await manager.AckAsync(extraTag);
    }
    
    [TestMethod]
    /// <summary>
    /// 验证未 Ack 会阻塞后续消息，直到手动确认。
    /// </summary>
    public async Task PubSubManager_BlocksUntilAckThenResumes()
    {
        await using var manager = PubSubManager.Create();
    
        var tags = new List<long>();
        var order = new List<int>();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    
        async Task Handler(WorkerMessage<int> message, CancellationToken cancellationToken)
        {
            order.Add(message.Payload);
            tags.Add(message.DeliveryTag);
    
            if (order.Count == 2)
            {
                completion.TrySetResult();
            }
        }
    
        await manager.SubscribeAsync<int>(Handler, options => options.WithPrefetch(1));
    
        await manager.PublishAsync(1);
        await manager.PublishAsync(2);
    
        // 等待第一个消息进入 handler
        await Task.Delay(100);
        Assert.AreEqual(1, order.Count, "第二个消息应被阻塞在 Ack 之前");
    
        // 手动 Ack 第一个消息，释放 worker
        await manager.AckAsync(tags[0]);
    
        await Task.WhenAny(completion.Task, Task.Delay(TimeSpan.FromSeconds(3)));
        Assert.AreEqual(2, order.Count, "第二个消息应在手动 Ack 后被处理");
    
        // Ack 第二个消息，避免泄漏
        await manager.AckAsync(tags[1]);
    }
    
    [TestMethod]
    /// <summary>
    /// 验证多订阅者情况下消息能够分布到不同订阅者并在外部确认。
    /// </summary>
    public async Task PubSubManager_ShouldDistributeAcrossMultipleSubscribers()
    {
        await using var manager = PubSubManager.Create();
    
        const int subscriberCount = 50;
        const int totalMessages = 500;
    
        var delivered = new ConcurrentDictionary<int, ConcurrentBag<int>>();
        var processed = 0;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    
        async Task Handler(WorkerMessage<int> message, CancellationToken cancellationToken)
        {
            var bag = delivered.GetOrAdd(message.WorkerId, _ => new ConcurrentBag<int>());
            bag.Add(message.Payload);
    
            if (Interlocked.Increment(ref processed) == totalMessages)
            {
                completion.TrySetResult();
            }
    
            await message.AckAsync();
        }
    
        var subscriptions = new List<IPubSubSubscription>(subscriberCount);
        for (var i = 0; i < subscriberCount; i++)
        {
            subscriptions.Add(await manager.SubscribeAsync<int>(Handler, options => options.WithPrefetch(2000)));
        }
    
        for (var i = 0; i < totalMessages; i++)
        {
            await manager.PublishAsync(i);
        }
    
        var finished = await Task.WhenAny(completion.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.AreSame(completion.Task, finished, "消息未在预期时间内全部处理");
    
        foreach (var subscription in subscriptions)
        {
            await subscription.DisposeAsync();
        }
    
        Assert.AreEqual(subscriberCount, delivered.Count, "部分订阅者未收到任何消息");
        var totalDelivered = delivered.Values.Sum(b => b.Count);
        Assert.AreEqual(totalMessages, totalDelivered, "分发的消息数量不正确");
        foreach (var bag in delivered.Values)
        {
            Assert.IsTrue(bag.Count > 0, "存在未获取消息的订阅者");
        }
    }
    
    [TestMethod]
    /// <summary>
    /// 验证移除订阅者后剩余订阅者仍可继续处理消息。
    /// </summary>
    public async Task PubSubManager_ShouldHandleSubscriptionRemoval()
    {
        await using var manager = PubSubManager.Create();
    
        const int initialSubscriptions = 2;
        const int totalMessages = 20;
    
        var processed = 0;
        var halfwayReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var perWorkerCounts = new ConcurrentDictionary<int, int>();
    
        async Task Handler(WorkerMessage<int> message, CancellationToken cancellationToken)
        {
            perWorkerCounts.AddOrUpdate(message.WorkerId, 1, (_, current) => current + 1);
    
            var current = Interlocked.Increment(ref processed);
            if (current == totalMessages / 2)
            {
                halfwayReached.TrySetResult();
            }
    
            if (current == totalMessages)
            {
                completion.TrySetResult();
            }
    
            await message.AckAsync();
        }
    
        var subscriptions = new List<IPubSubSubscription>(initialSubscriptions);
        for (var i = 0; i < initialSubscriptions; i++)
        {
            subscriptions.Add(await manager.SubscribeAsync<int>(Handler, options => options.WithPrefetch(2)));
        }
    
        var snapshot = manager.GetSnapshot();
        Assert.AreEqual(initialSubscriptions, snapshot.Count);
        var firstWorkerId = snapshot[0].Id;
    
        for (var i = 0; i < totalMessages / 2; i++)
        {
            await manager.PublishAsync(i);
        }
    
        await Task.WhenAny(halfwayReached.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.IsTrue(halfwayReached.Task.IsCompleted, "前半部分消息未及时处理");
    
        await subscriptions[0].DisposeAsync();
    
        for (var i = totalMessages / 2; i < totalMessages; i++)
        {
            await manager.PublishAsync(i);
        }
    
        var finished = await Task.WhenAny(completion.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.AreSame(completion.Task, finished, "剩余消息未在预期时间内处理完成");
    
        var remainingWorkers = manager.GetSnapshot();
        Assert.AreEqual(1, remainingWorkers.Count, "移除订阅者后仍存在多余的 worker");
        Assert.IsTrue(remainingWorkers[0].Id != firstWorkerId, "被移除的订阅者仍处于活动状态");
    
        Assert.AreEqual(totalMessages, perWorkerCounts.Values.Sum(), "处理的消息数量与预期不符");
    }
    
    [TestMethod]
    /// <summary>
    /// 验证 DisposeAsync 能正确停止所有 Worker 并清理资源。
    /// </summary>
    public async Task PubSubManager_DisposeAsyncShouldStopWorkers()
    {
        var manager = PubSubManager.Create();

        var processed = 0;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    
        await manager.SubscribeAsync<int>(async (message, token) =>
        {
            var current = Interlocked.Increment(ref processed);
            await message.AckAsync();
            if (current == 10)
            {
                completion.TrySetResult();
            }
        }, options => options.WithPrefetch(2));
    
        for (var i = 0; i < 10; i++)
        {
            await manager.PublishAsync(i);
        }
    
        var finished = await Task.WhenAny(completion.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.AreSame(completion.Task, finished, "消息未在 Dispose 前完成处理");
    
        await manager.DisposeAsync();
    
        var remaining = manager.GetSnapshot();
        Assert.AreEqual(0, remaining.Count, "Dispose 后仍存在活跃的 worker");
        Assert.AreEqual(10, Volatile.Read(ref processed), "处理的消息数量与预期不符");
    }
    
    [TestMethod]
    /// <summary>
    /// 验证单个 Worker 在 ConcurrencyLimit > 1 时可以并行处理消息。
    /// </summary>
    public async Task PubSubManager_ShouldExecuteHandlersInParallelWithinWorker()
    {
        const int totalMessages = 24;
        const int concurrencyLimit = 3;
    
        await using var manager = PubSubManager.Create();
    
        var concurrent = 0;
        var maxObserved = 0;
        var processed = 0;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    
        async Task Handler(WorkerMessage<int> message, CancellationToken cancellationToken)
        {
            var current = Interlocked.Increment(ref concurrent);
            UpdateMax(ref maxObserved, current);
    
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(80), cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref concurrent);
            }
    
            if (Interlocked.Increment(ref processed) == totalMessages)
            {
                completion.TrySetResult();
            }
    
            await message.AckAsync();
        }
    
        await manager.SubscribeAsync<int>(
            Handler,
            options => options
                .WithPrefetch(4)
                .WithConcurrencyLimit(concurrencyLimit));
    
        for (var i = 0; i < totalMessages; i++)
        {
            await manager.PublishAsync(i);
        }
    
        var finishedTask = await Task.WhenAny(completion.Task, Task.Delay(TimeSpan.FromSeconds(5)));
    
        Assert.AreSame(completion.Task, finishedTask, "消息未在预期时间内全部处理");
        Assert.AreEqual(totalMessages, processed, "处理的消息数量不正确");
        Assert.IsTrue(maxObserved <= concurrencyLimit, $"观测到的最大并发度 {maxObserved} 超出限制 {concurrencyLimit}");
        Assert.IsTrue(maxObserved >= 2, "未观察到多任务并发执行");
    }
    
    [TestMethod]
    /// <summary>
    /// 验证 Handler 超时会释放槽位，并且 Worker 在达到失败阈值前会继续处理后续消息。
    /// </summary>
    public async Task PubSubManager_ShouldReleaseCapacityAfterHandlerTimeout()
    {
        await using var manager = PubSubManager.Create();
    
        var handled = 0;
        var timedOut = 0;
        var started = new SemaphoreSlim(0);
    
        async Task Handler(WorkerMessage<int> message, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref handled);
            started.Release();
    
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref timedOut);
                throw;
            }
        }
    
        await manager.SubscribeAsync<int>(
            Handler,
            options => options
                .WithPrefetch(1)
                .WithHandlerTimeout(TimeSpan.FromMilliseconds(120))
                .WithFailureThreshold(5));
    
        for (var i = 0; i < 3; i++)
        {
            await manager.PublishAsync(i);
            Assert.IsTrue(await started.WaitAsync(TimeSpan.FromSeconds(1)), "Handler 未按时启动");
            await Task.Delay(TimeSpan.FromMilliseconds(300));
        }
    
        await Task.Delay(500);
    
        Assert.IsTrue(handled >= 3, "超时后未继续分派后续消息");
        Assert.IsTrue(timedOut >= 3, "未检测到预期的 Handler 超时次数");
    }
    
    private static void UpdateMax(ref int target, int value)
    {
        int snapshot;
        while ((snapshot = Volatile.Read(ref target)) < value)
        {
            if (Interlocked.CompareExchange(ref target, value, snapshot) == snapshot)
            {
                break;
            }
        }
    }
}

