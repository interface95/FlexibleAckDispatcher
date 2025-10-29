# Worker 选择策略使用指南

## 概述

FlexibleAckDispatcher 现在支持可插拔的 Worker 选择策略，允许你自定义消息如何分配给不同的 Worker。

## 内置策略

### 1. 最少连接数策略（LeastConnectionsSelectionStrategy）

**默认策略**，优先将消息分配给当前并发任务数最少的 Worker。

**适用场景**：
- 需要动态负载均衡
- Worker 处理能力相近
- 希望最大化系统吞吐量

**使用方式**：
```csharp
// 方式1：使用默认策略（无需配置）
var manager = PubSubManager.Create();

// 方式2：显式配置
var manager = PubSubManager.Create(options =>
    options.WithSelectionStrategy(() => new LeastConnectionsSelectionStrategy()));
```

### 2. 轮询策略（RoundRobinSelectionStrategy）

按顺序轮流选择 Worker，实现平均分配。

**适用场景**：
- 需要均匀分布消息
- Worker 处理能力相同
- 希望预测性强的分配模式

**使用方式**：
```csharp
var manager = PubSubManager.Create(options =>
    options.WithSelectionStrategy(() => new RoundRobinSelectionStrategy()));
```

## 策略对比

| 特性 | LeastConnectionsSelectionStrategy | RoundRobinSelectionStrategy |
|------|----------------------------------|----------------------------|
| **分配原则** | 基于当前负载 | 按固定顺序 |
| **负载均衡** | 动态自适应 | 静态均匀 |
| **适合场景** | 任务执行时间差异大 | 任务执行时间相近 |
| **性能开销** | 稍高（需维护优先队列） | 较低（简单轮询） |
| **预测性** | 低 | 高 |

## 完整示例

### 示例1：使用默认的最少连接数策略

```csharp
using InMemoryWorkerBalancer;

await using var manager = PubSubManager.Create();

// 添加多个 Worker
await manager.SubscribeAsync<MyMessage>(async (message, ct) =>
{
    // 处理消息
    await ProcessAsync(message.Payload);
    await message.AckAsync();
}, options => options
    .WithName("Worker1")
    .WithConcurrencyLimit(5));

await manager.SubscribeAsync<MyMessage>(async (message, ct) =>
{
    await ProcessAsync(message.Payload);
    await message.AckAsync();
}, options => options
    .WithName("Worker2")
    .WithConcurrencyLimit(3));

// 发布消息 - 自动分配给负载较低的 Worker
for (int i = 0; i < 100; i++)
{
    await manager.PublishAsync(new MyMessage { Id = i });
}
```

### 示例2：使用轮询策略

```csharp
using InMemoryWorkerBalancer;
using InMemoryWorkerBalancer.Internal;

await using var manager = PubSubManager.Create(options =>
    options.WithSelectionStrategy(() => new RoundRobinSelectionStrategy()));

// Worker 1
await manager.SubscribeAsync<MyMessage>(async (message, ct) =>
{
    Console.WriteLine($"Worker 1 处理: {message.Payload.Id}");
    await message.AckAsync();
});

// Worker 2
await manager.SubscribeAsync<MyMessage>(async (message, ct) =>
{
    Console.WriteLine($"Worker 2 处理: {message.Payload.Id}");
    await message.AckAsync();
});

// Worker 3
await manager.SubscribeAsync<MyMessage>(async (message, ct) =>
{
    Console.WriteLine($"Worker 3 处理: {message.Payload.Id}");
    await message.AckAsync();
});

// 消息会按 Worker1 -> Worker2 -> Worker3 -> Worker1 的顺序分配
for (int i = 0; i < 9; i++)
{
    await manager.PublishAsync(new MyMessage { Id = i });
}
```

## 自定义策略

你可以实现 `IWorkerSelectionStrategy` 接口来创建自定义策略。

### 接口定义

```csharp
public interface IWorkerSelectionStrategy : IDisposable
{
    /// <summary>
    /// 当前可用的空闲 Worker 数量。
    /// </summary>
    int IdleCount { get; }

    /// <summary>
    /// 调度队列中的 Worker 数量。
    /// </summary>
    int QueueLength { get; }

    /// <summary>
    /// 更新指定 Worker 的最新快照信息。
    /// </summary>
    void Update(WorkerEndpointSnapshot snapshot);

    /// <summary>
    /// 尝试租用一个可用的 Worker。
    /// </summary>
    bool TryRent(out int workerId);

    /// <summary>
    /// 等待直到有 Worker 可用。
    /// </summary>
    Task WaitForWorkerAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Worker 被移除时的清理逻辑。
    /// </summary>
    void Remove(int workerId);
}
```

### 自定义策略示例：随机选择

```csharp
public class RandomSelectionStrategy : IWorkerSelectionStrategy
{
    private readonly object _lock = new();
    private readonly List<int> _availableWorkers = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly Random _random = new();
    private int _disposed;

    public int IdleCount => _availableWorkers.Count;
    public int QueueLength => _availableWorkers.Count;

    public void Update(WorkerEndpointSnapshot snapshot)
    {
        lock (_lock)
        {
            if (!_availableWorkers.Contains(snapshot.Id) && 
                snapshot.IsActive && 
                snapshot.CurrentConcurrency < snapshot.MaxConcurrency)
            {
                _availableWorkers.Add(snapshot.Id);
                _signal.Release();
            }
        }
    }

    public bool TryRent(out int workerId)
    {
        workerId = -1;
        
        if (!_signal.Wait(0))
            return false;

        lock (_lock)
        {
            if (_availableWorkers.Count == 0)
            {
                _signal.Release();
                return false;
            }

            var index = _random.Next(_availableWorkers.Count);
            workerId = _availableWorkers[index];
            _availableWorkers.RemoveAt(index);
            return true;
        }
    }

    public async Task WaitForWorkerAsync(CancellationToken cancellationToken)
    {
        await _signal.WaitAsync(cancellationToken);
        _signal.Release();
    }

    public void Remove(int workerId)
    {
        lock (_lock)
        {
            _availableWorkers.Remove(workerId);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _signal.Dispose();
    }
}
```

### 使用自定义策略

```csharp
var manager = PubSubManager.Create(options =>
    options.WithSelectionStrategy(() => new RandomSelectionStrategy()));
```

## 性能考虑

1. **最少连接数策略**
   - 使用优先队列，时间复杂度 O(log n)
   - 适合 Worker 数量较多（< 1000）的场景

2. **轮询策略**
   - 简单索引访问，时间复杂度 O(1)
   - 适合高频分发场景

3. **自定义策略**
   - 根据具体实现而定
   - 建议使用线程安全的数据结构
   - 避免在 `TryRent` 中执行耗时操作

## 最佳实践

1. **选择合适的策略**
   - 默认使用最少连接数策略
   - 任务均匀时考虑轮询策略
   - 特殊需求时实现自定义策略

2. **监控指标**
   ```csharp
   Console.WriteLine($"空闲 Worker 数: {manager.IdleCount}");
   Console.WriteLine($"执行中任务数: {manager.RunningCount}");
   Console.WriteLine($"已调度任务数: {manager.DispatchedCount}");
   Console.WriteLine($"已完成任务数: {manager.CompletedCount}");
   ```

3. **动态调整**
   - 根据实际负载情况调整 Worker 并发限制
   - 使用 Worker 快照监控分配情况
   ```csharp
   var snapshot = manager.GetSnapshot();
   foreach (var worker in snapshot)
   {
       Console.WriteLine($"Worker {worker.Id}: {worker.CurrentConcurrency}/{worker.MaxConcurrency}");
   }
   ```

## 故障排查

### 问题：消息分配不均

**可能原因**：
- Worker 并发限制设置不合理
- 某些 Worker 处理速度过慢

**解决方案**：
- 检查各 Worker 的并发限制配置
- 查看 Worker 快照，分析负载分布
- 考虑使用轮询策略

### 问题：吞吐量低

**可能原因**：
- Worker 数量不足
- 并发限制过低

**解决方案**：
- 增加 Worker 数量或提高并发限制
- 使用最少连接数策略优化分配

## 总结

Worker 选择策略为消息分发提供了灵活的控制能力：
- **默认策略**适合大多数场景
- **轮询策略**适合均匀分配需求
- **自定义策略**满足特殊业务需求

根据实际场景选择合适的策略，可以显著提升系统性能和资源利用率。

