# FlexibleAckDispatcher

一个高性能、灵活的内存消息调度系统，基于 .NET 实现的发布-订阅模式（Pub/Sub），支持灵活的消息确认机制、并发控制和负载均衡。

## 🌟 核心特性

- ✅ **灵活的 ACK 机制**：支持在处理函数内部或外部手动确认消息
- 🚀 **高性能异步处理**：基于 `System.Threading.Channels` 实现的高效消息队列
- 🔄 **负载均衡**：多订阅者自动分发消息，实现 Worker 级别的负载均衡
- 🎯 **并发控制**：支持 Prefetch 和 ConcurrencyLimit 精细控制消息处理并发度
- ⏱️ **超时保护**：内置消息处理超时机制，防止 Worker 阻塞
- 📊 **故障隔离**：Worker 级别的失败阈值，超过阈值自动停止 Worker
- 🔌 **热插拔**：支持动态添加和移除订阅者

## 📦 项目结构

```
FlexibleAckDispatcher/
├── src/
│   └── InMemoryWorkerBalancer/           # 核心库
│       ├── Abstractions/                  # 抽象接口
│       │   ├── IHandler.cs               # 消息处理器接口
│       │   ├── IPubSubChannel.cs         # 发布通道接口
│       │   ├── IPubSubManager.cs         # 管理器接口
│       │   └── IPubSubSubscription.cs    # 订阅句柄接口
│       ├── Internal/                      # 内部实现
│       │   ├── ReusableTimeoutScope.cs   # 可重用的超时作用域
│       │   ├── SnowflakeIdGenerator.cs   # 雪花 ID 生成器
│       │   └── WorkerProcessor.cs        # Worker 处理器
│       ├── PubSubManager.cs              # 发布订阅管理器（核心）
│       ├── WorkerManager.cs              # Worker 生命周期管理
│       ├── WorkerDispatcher.cs           # 消息调度器
│       ├── WorkerMessage.cs              # 消息包装器
│       ├── WorkerEndpoint.cs             # Worker 端点
│       ├── WorkerCapacity.cs             # Worker 容量控制
│       ├── WorkerAckToken.cs             # ACK 令牌
│       ├── SubscriptionOptions.cs        # 订阅配置选项
│       └── WorkerProcessingDelegate.cs   # 处理委托定义
└── Test/
    └── TestWorkerBalancerPubSub.cs       # 单元测试
```

## 🚀 快速开始

### 1. 基本用法

```csharp
using InMemoryWorkerBalancer;

// 创建 PubSubManager
await using var manager = new PubSubManager<int>();

// 订阅消息（内部自动 ACK）
await manager.SubscribeAsync(async (message, cancellationToken) =>
{
    Console.WriteLine($"处理消息: {message.Payload}");
    await message.AckAsync(); // 手动确认
});

// 发布消息
for (int i = 0; i < 10; i++)
{
    await manager.Channel.PublishAsync(i);
}
```

### 2. 配置订阅选项

```csharp
await manager.SubscribeAsync(
    async (message, cancellationToken) =>
    {
        // 处理消息
        Console.WriteLine($"Worker {message.WorkerId} 处理: {message.Payload}");
        await message.AckAsync();
    },
    options => options
        .WithName("MyWorker")                           // Worker 名称
        .WithPrefetch(10)                                // 预取 10 条消息
        .WithConcurrencyLimit(5)                         // 最大并发 5
        .WithHandlerTimeout(TimeSpan.FromSeconds(30))    // 超时 30 秒
        .WithFailureThreshold(3)                         // 失败阈值 3 次
);
```

### 3. 外部手动 ACK

```csharp
var deliveryTags = new List<long>();

// 订阅但不立即 ACK
await manager.SubscribeAsync(async (message, cancellationToken) =>
{
    deliveryTags.Add(message.DeliveryTag);
    // 处理消息但不 ACK
    Console.WriteLine($"收到消息: {message.Payload}");
});

// 发布消息
await manager.Channel.PublishAsync(42);

// 稍后在外部确认
await manager.AckAsync(deliveryTags[0]);
```

### 4. 多订阅者负载均衡

```csharp
// 创建 3 个订阅者
for (int i = 0; i < 3; i++)
{
    int workerId = i;
    await manager.SubscribeAsync(
        async (message, cancellationToken) =>
        {
            Console.WriteLine($"订阅者 {workerId} 处理: {message.Payload}");
            await Task.Delay(100); // 模拟处理
            await message.AckAsync();
        },
        options => options.WithPrefetch(5)
    );
}

// 发布 30 条消息，自动分配到 3 个订阅者
for (int i = 0; i < 30; i++)
{
    await manager.Channel.PublishAsync(i);
}
```

### 5. 使用接口方式

```csharp
using InMemoryWorkerBalancer.Abstractions;

// 实现 IWorkerMessageHandler 接口
public class MyMessageHandler : IWorkerMessageHandler<string>
{
    public async Task HandleAsync(WorkerMessage<string> message, CancellationToken cancellationToken)
    {
        Console.WriteLine($"处理消息: {message.Payload}");
        await Task.Delay(100, cancellationToken);
        await message.AckAsync();
    }
}

// 使用接口订阅
var handler = new MyMessageHandler();
await manager.SubscribeAsync(handler, options => options.WithPrefetch(5));
```

## 📚 核心类详解

### PubSubManager&lt;T&gt;

发布订阅管理器，系统的核心入口。

**主要方法：**
- `SubscribeAsync()` - 注册订阅者
- `Channel.PublishAsync()` - 发布消息
- `AckAsync()` - 外部确认消息
- `DisposeAsync()` - 释放资源

**属性：**
- `Channel` - 发布通道
- `SubscriberCount` - 当前订阅者数量

### WorkerMessage&lt;T&gt;

消息包装器，包含消息负载和确认方法。

**属性：**
- `Payload` - 消息内容
- `DeliveryTag` - 全局唯一的投递标签（基于雪花算法）
- `WorkerId` - 处理该消息的 Worker ID

**方法：**
- `AckAsync()` - 确认消息处理完成

### SubscriptionOptions

订阅配置选项，支持链式调用。

**配置方法：**
- `WithName(string)` - 设置 Worker 名称
- `WithPrefetch(int)` - 设置预取数量（默认：1）
- `WithConcurrencyLimit(int)` - 设置最大并发数（默认：与 Prefetch 相同，最大：50）
- `WithHandlerTimeout(TimeSpan)` - 设置处理超时时间（默认：5 分钟）
- `WithFailureThreshold(int)` - 设置失败阈值（默认：3）

### WorkerManager&lt;T&gt;

Worker 生命周期管理器（内部使用）。

**功能：**
- Worker 注册与移除
- 消息投递追踪
- 容量控制与负载均衡
- ACK 管理

### WorkerDispatcher&lt;T&gt;

消息调度器，负责将消息从主通道分发到可用的 Worker。

### IPubSubSubscription

订阅句柄，用于管理单个订阅的生命周期。

**属性：**
- `Id` - 订阅者 ID

**方法：**
- `DisposeAsync()` - 取消订阅并释放资源

## 🎯 核心概念

### 1. Prefetch（预取）

控制每个 Worker 同时持有的未确认消息数量。较大的 Prefetch 可以提高吞吐量，但会占用更多内存。

```csharp
// Worker 最多持有 10 条未确认的消息
options.WithPrefetch(10)
```

### 2. ConcurrencyLimit（并发限制）

控制 Worker 内部同时处理消息的并发数。允许单个 Worker 并行处理多条消息。

```csharp
// Worker 可以同时处理 5 条消息
options.WithConcurrencyLimit(5)
```

**注意：**
- `ConcurrencyLimit` 不能超过 `Prefetch`
- `ConcurrencyLimit` 最大值为 50
- 如果未设置 `ConcurrencyLimit`，默认等于 `Prefetch`

### 3. ACK 机制

消息必须被确认后才会释放 Worker 槽位。支持两种确认方式：

**方式一：内部确认**
```csharp
await manager.SubscribeAsync(async (message, cancellationToken) =>
{
    // 处理消息
    await message.AckAsync(); // 在处理函数内确认
});
```

**方式二：外部确认**
```csharp
await manager.SubscribeAsync(async (message, cancellationToken) =>
{
    // 不在这里确认
});

// 稍后在外部确认
await manager.AckAsync(deliveryTag);
```

### 4. 超时保护

当消息处理超过 `HandlerTimeout` 时，系统会取消该任务并释放槽位。

```csharp
options.WithHandlerTimeout(TimeSpan.FromSeconds(30))
```

### 5. 失败阈值

Worker 连续失败或超时达到阈值后会自动停止，防止故障扩散。

```csharp
options.WithFailureThreshold(3) // 连续失败 3 次后停止
```

## 🔧 高级用法

### 动态添加/移除订阅者

```csharp
// 添加订阅者
var subscription = await manager.SubscribeAsync(handler);

// 移除订阅者
await subscription.DisposeAsync();
```

### 监控 Worker 状态

```csharp
// 获取当前订阅者数量
Console.WriteLine($"当前订阅者: {manager.SubscriberCount}");
```

### 优雅关闭

```csharp
// 等待所有消息处理完成后释放资源
await manager.DisposeAsync();
```

## ⚠️ 注意事项

1. **必须确认消息**：未确认的消息会一直占用 Worker 槽位，导致系统阻塞
2. **避免重复确认**：同一消息多次确认会抛出异常
3. **Prefetch 与内存**：Prefetch 越大，内存占用越多，需根据实际情况调整
4. **并发与资源**：ConcurrencyLimit 决定了资源消耗，需要权衡性能与资源
5. **超时设置**：HandlerTimeout 应根据实际业务处理时间合理设置

## 🧪 测试

项目包含完整的单元测试，覆盖以下场景：

- ✅ 基本消息处理
- ✅ Prefetch 限制验证
- ✅ 并发控制验证
- ✅ 外部手动 ACK
- ✅ 多订阅者负载均衡
- ✅ 订阅者动态添加/移除
- ✅ 超时保护
- ✅ 资源清理

运行测试：

```bash
cd Test
dotnet test
```

## 📊 性能特点

- **零拷贝**：基于 `System.Threading.Channels` 实现，避免不必要的数据拷贝
- **无锁设计**：核心路径使用 `ConcurrentDictionary` 和 `Channel`，减少锁竞争
- **异步优先**：全异步 API，充分利用 .NET 异步机制
- **内存高效**：使用对象池和可重用结构减少 GC 压力

## 🛠️ 技术栈

- **.NET 8.0 / 9.0**
- **C# 12** (latest)
- **System.Threading.Channels** - 高性能异步队列
- **Microsoft.Extensions.Logging** - 日志抽象

## 📄 许可证

本项目采用 MIT 许可证。

## 🤝 贡献

欢迎提交 Issue 和 Pull Request！

---

**作者**: FlexibleAckDispatcher Team  
**最后更新**: 2025-10-28

