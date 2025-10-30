using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FlexibleAckDispatcher;
using FlexibleAckDispatcher.InMemory;
using FlexibleAckDispatcher.InMemory.Internal;

namespace TestProject2;

[TestClass]
public sealed class TestWorkerSelectionStrategies
{
    [TestMethod]
    /// <summary>
    /// 验证默认使用优先队列策略（最少并发数调度）。
    /// </summary>
    public async Task PubSubManager_ShouldUsePriorityQueueStrategyByDefault()
    {
        await using var manager = PubSubManager.Create();

        var worker1Started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker1Release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondMessageWorkerId = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker1Id = 0;
        var worker2Id = 0;

        // 第一个 Worker：占用一个槽位
        await manager.SubscribeAsync<int>(async (message, cancellationToken) =>
        {
            worker1Id = message.WorkerId;
            worker1Started.TrySetResult();
            await worker1Release.Task.WaitAsync(cancellationToken);
            await message.AckAsync();
        }, options => options
            .WithName("Worker1")
            .WithPrefetch(10)
            .WithConcurrencyLimit(5));

        await manager.PublishAsync(1);
        await worker1Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        // 第二个 Worker：无负载
        await manager.SubscribeAsync<int>(async (message, cancellationToken) =>
        {
            worker2Id = message.WorkerId;
            await message.AckAsync();
            secondMessageWorkerId.TrySetResult(message.WorkerId);
        }, options => options
            .WithName("Worker2")
            .WithPrefetch(10)
            .WithConcurrencyLimit(5));

        // 发送第二条消息，应该被分配给连接数较少的 Worker2
        await manager.PublishAsync(2);
        var assignedWorkerId = await secondMessageWorkerId.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.AreNotEqual(0, worker1Id, "Worker1 未接收到消息");
        Assert.AreNotEqual(0, worker2Id, "Worker2 未初始化");
        Assert.AreNotEqual(worker1Id, worker2Id, "两个 Worker 应具有不同的标识");
        Assert.AreEqual(worker2Id, assignedWorkerId, "优先队列策略应优先选择负载较低的 Worker2");

        worker1Release.TrySetResult();
        await Task.Delay(100);
    }

    [TestMethod]
    /// <summary>
    /// 验证轮询策略能够均匀分配消息。
    /// </summary>
    public async Task PubSubManager_ShouldUseRoundRobinStrategyWhenConfigured()
    {
        await using var manager = PubSubManager.Create(options =>
            options.WithSelectionStrategy(() => new RoundRobinSelectionStrategy()));

        const int workerCount = 3;
        const int totalMessages = 30;

        var workerMessageCounts = new ConcurrentDictionary<int, int>();
        var processed = 0;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task Handler(WorkerMessage<int> message, CancellationToken cancellationToken)
        {
            workerMessageCounts.AddOrUpdate(message.WorkerId, 1, (_, count) => count + 1);

            if (Interlocked.Increment(ref processed) == totalMessages)
            {
                completion.TrySetResult();
            }

            await message.AckAsync();
        }

        // 创建多个 Worker
        for (var i = 0; i < workerCount; i++)
        {
            await manager.SubscribeAsync<int>(Handler, options => options
                .WithName($"Worker{i}")
                .WithPrefetch(5)
                .WithConcurrencyLimit(1));
        }

        // 发送消息
        for (var i = 0; i < totalMessages; i++)
        {
            await manager.PublishAsync(i);
        }

        var finished = await Task.WhenAny(completion.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.AreSame(completion.Task, finished, "消息未在预期时间内全部处理");

        // 验证消息分布
        Assert.AreEqual(workerCount, workerMessageCounts.Count, "部分 Worker 未收到消息");
        
        var counts = workerMessageCounts.Values.ToArray();
        var minCount = counts.Min();
        var maxCount = counts.Max();
        
        // 轮询策略应该使消息分布相对均匀
        Assert.IsTrue(maxCount - minCount <= totalMessages / workerCount, 
            $"轮询策略下消息分布不均：最少 {minCount}，最多 {maxCount}");
    }

    [TestMethod]
    /// <summary>
    /// 验证优先队列策略在有负载时会优先选择空闲 Worker。
    /// </summary>
    public async Task PriorityQueueStrategy_ShouldPreferIdleWorkers()
    {
        await using var manager = PubSubManager.Create(options =>
            options.WithSelectionStrategy(() => new PriorityQueueWorkerSelectionStrategy()));

        const int totalMessages = 20;
        var worker1Processing = new SemaphoreSlim(0);
        var worker1Continue = new SemaphoreSlim(0);
        var worker2Messages = new ConcurrentBag<int>();
        var processed = 0;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Worker1：慢速处理，模拟高负载
        await manager.SubscribeAsync<int>(async (message, cancellationToken) =>
        {
            worker1Processing.Release();
            await worker1Continue.WaitAsync(cancellationToken);
            await message.AckAsync();

            if (Interlocked.Increment(ref processed) == totalMessages)
            {
                completion.TrySetResult();
            }
        }, options => options
            .WithName("SlowWorker")
            .WithPrefetch(10)
            .WithConcurrencyLimit(1));

        // 发送第一条消息给 Worker1
        await manager.PublishAsync(0);
        await worker1Processing.WaitAsync(TimeSpan.FromSeconds(1));

        // Worker2：快速处理，空闲
        await manager.SubscribeAsync<int>(async (message, cancellationToken) =>
        {
            worker2Messages.Add(message.Payload);
            await message.AckAsync();

            if (Interlocked.Increment(ref processed) == totalMessages)
            {
                completion.TrySetResult();
            }
        }, options => options
            .WithName("FastWorker")
            .WithPrefetch(10)
            .WithConcurrencyLimit(5));

        // 发送更多消息，应该优先被分配给空闲的 Worker2
        for (var i = 1; i < totalMessages; i++)
        {
            await manager.PublishAsync(i);
        }

        // 等待一段时间让消息分配
        await Task.Delay(300);

        // 释放 Worker1
        worker1Continue.Release();

        await Task.WhenAny(completion.Task, Task.Delay(TimeSpan.FromSeconds(5)));

        // Worker2 应该处理了大部分消息（因为 Worker1 在忙）
        Assert.IsTrue(worker2Messages.Count >= totalMessages - 5, 
            $"优先队列策略应优先分配给空闲 Worker，但 Worker2 只处理了 {worker2Messages.Count}/{totalMessages - 1} 条消息");
    }

    // [TestMethod] - Temporarily disabled for debugging
    // /// <summary>
    // /// 验证可以自定义选择策略。
    // /// </summary>
    // public async Task PubSubManager_ShouldSupportCustomSelectionStrategy()
    // {
    //     // 使用轮询策略作为自定义策略的示例
    //     await using var manager = PubSubManager.Create(options =>
    //         options.WithSelectionStrategy(() => new RoundRobinSelectionStrategy()));
    //
    //     var processed = 0;
    //     var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    //
    //     async Task Handler(WorkerMessage<int> message, CancellationToken cancellationToken)
    //     {
    //         if (Interlocked.Increment(ref processed) == 10)
    //         {
    //             completion.TrySetResult();
    //         }
    //
    //         await message.AckAsync();
    //     }
    //
    //     await manager.SubscribeAsync<int>(Handler, options => options.WithPrefetch(10).WithConcurrencyLimit(5));
    //
    //     for (var i = 0; i < 10; i++)
    //     {
    //         await manager.PublishAsync(i);
    //     }
    //
    //     var finished = await Task.WhenAny(completion.Task, Task.Delay(TimeSpan.FromSeconds(5)));
    //     Assert.AreSame(completion.Task, finished, "自定义策略未正常工作");
    //     Assert.AreEqual(10, processed, "未处理所有消息");
    // }

    [TestMethod]
    /// <summary>
    /// 验证策略在 Worker 动态增删时的正确性。
    /// </summary>
    public async Task SelectionStrategy_ShouldHandleDynamicWorkerChanges()
    {
        await using var manager = PubSubManager.Create(options =>
            options.WithSelectionStrategy(() => new PriorityQueueWorkerSelectionStrategy()));

        var processed = 0;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task Handler(WorkerMessage<int> message, CancellationToken cancellationToken)
        {
            await Task.Delay(10, cancellationToken);
            if (Interlocked.Increment(ref processed) == 30)
            {
                completion.TrySetResult();
            }
            await message.AckAsync();
        }

        // 初始 2 个 Worker
        var subscription1 = await manager.SubscribeAsync<int>(Handler, options => options.WithPrefetch(5));
        var subscription2 = await manager.SubscribeAsync<int>(Handler, options => options.WithPrefetch(5));

        Assert.AreEqual(2, manager.SubscriberCount);

        // 发送一些消息
        for (var i = 0; i < 10; i++)
        {
            await manager.PublishAsync(i);
        }

        await Task.Delay(100);

        // 添加第三个 Worker
        var subscription3 = await manager.SubscribeAsync<int>(Handler, options => options.WithPrefetch(5));
        Assert.AreEqual(3, manager.SubscriberCount);

        // 继续发送消息
        for (var i = 10; i < 20; i++)
        {
            await manager.PublishAsync(i);
        }

        await Task.Delay(100);

        // 移除一个 Worker
        await subscription1.DisposeAsync();
        Assert.AreEqual(2, manager.SubscriberCount);

        // 继续发送消息
        for (var i = 20; i < 30; i++)
        {
            await manager.PublishAsync(i);
        }

        var finished = await Task.WhenAny(completion.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.AreSame(completion.Task, finished, "动态增删 Worker 后消息未正常处理");
        Assert.AreEqual(30, processed);
    }
}

