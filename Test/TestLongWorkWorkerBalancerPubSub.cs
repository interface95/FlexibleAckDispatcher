// using System.Collections.Concurrent;
// using InMemoryWorkerBalancer;
// using InMemoryWorkerBalancer.Abstractions;
//
// namespace TestProject2;
//
// [TestClass]
// public class TestLongWorkWorkerBalancerPubSub
// {
//     [TestMethod]
//     /// <summary>
//     /// 验证多订阅者情况下消息能够分布到不同订阅者并在外部确认。
//     /// </summary>
//     public async Task PubSubManager_ShouldDistributeAcrossMultipleSubscribers()
//     {
//         await using var manager = PubSubManager.Create();
//
//         const int subscriberCount = 50;
//         const int totalMessages = 500000;
//
//         var delivered = new ConcurrentDictionary<int, ConcurrentBag<int>>();
//         var processed = 0;
//         var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
//
//         async Task Handler(WorkerMessage<int> message, CancellationToken cancellationToken)
//         {
//             var bag = delivered.GetOrAdd(message.WorkerId, _ => new ConcurrentBag<int>());
//             bag.Add(message.Payload);
//
//             if (Interlocked.Increment(ref processed) == totalMessages)
//             {
//                 completion.TrySetResult();
//             }
//             
//             await Task.Delay(Random.Shared.Next(100), cancellationToken);
//             
//             await message.AckAsync();
//         }
//
//         var subscriptions = new List<IPubSubSubscription>(subscriberCount);
//         for (var i = 0; i < subscriberCount; i++)
//         {
//             subscriptions.Add(await manager.SubscribeAsync<int>(Handler, options => options.WithPrefetch(2000)));
//         }
//
//         for (var i = 0; i < totalMessages; i++)
//         {
//             await manager.PublishAsync(i);
//         }
//
//         var finished = await Task.WhenAny(completion.Task);
//         Assert.AreSame(completion.Task, finished, "消息未在预期时间内全部处理");
//
//         foreach (var subscription in subscriptions)
//         {
//             await subscription.DisposeAsync();
//         }
//
//         Assert.AreEqual(subscriberCount, delivered.Count, "部分订阅者未收到任何消息");
//         var totalDelivered = delivered.Values.Sum(b => b.Count);
//         Assert.AreEqual(totalMessages, totalDelivered, "分发的消息数量不正确");
//         foreach (var bag in delivered.Values)
//         {
//             Assert.IsTrue(bag.Count > 0, "存在未获取消息的订阅者");
//         }
//     }
// }