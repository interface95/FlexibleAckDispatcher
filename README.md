# FlexibleAckDispatcher

一个高性能、灵活的内存消息调度系统，基于 .NET 实现的发布-订阅模式（Pub/Sub），支持灵活的消息确认机制、并发控制和负载均衡。

## 🌟 核心特性

- ✅ **灵活的 ACK 机制**：支持在处理函数内部或外部手动确认消息
- 🚀 **高性能异步处理**：基于 `System.Threading.Channels` 实现的高效消息队列
- 🔄 **负载均衡**：多订阅者自动分发消息，实现 Worker 级别的负载均衡
- 🎯 **并发控制**：支持 Prefetch 和 ConcurrencyLimit 精细控制消息处理并发度
- ⏱️ **超时保护**：内置消息处理超时机制，防止 Worker 阻塞
- 📊 **运行时指标**：公开订阅者数量、空闲 Worker 数、执行中任务数以及 Worker 快照
- 🔌 **热插拔**：支持动态添加和移除订阅者

## 📦 安装

### NuGet 包管理器

```bash
dotnet add package InMemoryWorkerBalancer
```

### Package Manager Console

```powershell
Install-Package InMemoryWorkerBalancer
```

### .csproj 文件

```xml
<PackageReference Include="InMemoryWorkerBalancer" Version="1.0.0-preview.1" />
```

## 📂 项目结构

```
FlexibleAckDispatcher/
├── src/
│   └── InMemoryWorkerBalancer/           # 核心库
│       ├── Abstractions/                  # 对外接口
│       │   ├── IWorkerMessageHandler.cs   # 消息处理器接口
│       │   ├── IPubSubChannel.cs          # 发布通道接口
│       │   ├── IPubSubManager.cs          # 管理器接口
│       │   └── IPubSubSubscription.cs     # 订阅句柄接口
│       ├── Internal/                      # 内部实现（Worker 管理、调度、ACK 等）
│       ├── JsonWorkerPayloadSerializer.cs # 默认 JSON 序列化器
│       ├── PubSubManager.cs               # 发布订阅管理器（核心）
│       ├── PubSubManagerOptions.cs        # 构建配置选项
│       ├── SubscriptionOptions.cs         # 订阅配置选项
│       ├── WorkerEndpointSnapshot.cs      # Worker 快照结构
│       └── WorkerMessage.cs               # 消息包装器
└── Test/
    └── TestWorkerBalancerPubSub.cs       # 单元测试
```

## 🚀 快速开始

### 1. 基本用法

```csharp
using InMemoryWorkerBalancer;

// 创建 PubSubManager，使用默认 JSON 序列化和 NullLogger
await using var manager = PubSubManager.Create();

// 订阅消息（内部自动 ACK）
await manager.SubscribeAsync<int>(async (message, cancellationToken) =>
{
    Console.WriteLine($"处理消息: {message.Payload}");
    await message.AckAsync();
});

// 发布消息
for (int i = 0; i < 10; i++)
{
    await manager.PublishAsync(i);
}
```

### 2. 通过 Options 构建实例

```csharp
await using var manager = PubSubManager.Create(options => options
    .WithLogger(logger)
    .WithSerializer(customSerializer)
    .OnWorkerAdded(snapshot =>
    {
        Console.WriteLine($"Worker {snapshot.Id} 加入，最大并发 {snapshot.MaxConcurrency}");
        return Task.CompletedTask;
    })
    .OnWorkerRemoved(snapshot =>
    {
        Console.WriteLine($"Worker {snapshot.Id} 离开");
        return Task.CompletedTask;
    }));
```

### 3. 配置订阅选项

```csharp
await manager.SubscribeAsync<int>(
    async (message, cancellationToken) =>
    {
        Console.WriteLine($"Worker {message.WorkerId} 处理: {message.Payload}");
        await message.AckAsync();
    },
    options => options
        .WithName("MyWorker")
        .WithPrefetch(10)
        .WithConcurrencyLimit(5)
        .WithHandlerTimeout(TimeSpan.FromSeconds(30))
        .WithFailureThreshold(3));
```

### 4. 外部手动 ACK

```csharp
var deliveryTags = new List<long>();

await manager.SubscribeAsync<int>(async (message, cancellationToken) =>
{
    deliveryTags.Add(message.DeliveryTag);
    Console.WriteLine($"收到消息: {message.Payload}");
});

await manager.PublishAsync(42);

// 稍后在外部确认
await manager.AckAsync(deliveryTags[0]);
```

### 5. 多订阅者负载均衡

```csharp
for (int i = 0; i < 3; i++)
{
    int workerId = i;
    await manager.SubscribeAsync<int>(
        async (message, cancellationToken) =>
        {
            Console.WriteLine($"订阅者 {workerId} 处理: {message.Payload}");
            await Task.Delay(100, cancellationToken);
            await message.AckAsync();
        },
        options => options.WithPrefetch(5));
}

for (int i = 0; i < 30; i++)
{
    await manager.PublishAsync(i);
}
```

### 6. 使用接口方式

```csharp
using InMemoryWorkerBalancer.Abstractions;

public class MyMessageHandler : IWorkerMessageHandler<string>
{
    public async Task HandleAsync(WorkerMessage<string> message, CancellationToken cancellationToken)
    {
        Console.WriteLine($"处理消息: {message.Payload}");
        await Task.Delay(100, cancellationToken);
        await message.AckAsync();
    }
}

var handler = new MyMessageHandler();
await manager.SubscribeAsync<string>(handler, options => options.WithPrefetch(5));
```

## 📊 运行时观测

```csharp
// 订阅者数量
Console.WriteLine(manager.SubscriberCount);

// 空闲 worker 数
Console.WriteLine(manager.IdleWorkerCount);

// 正在处理的任务总数
Console.WriteLine(manager.RunningTaskCount);

// Worker 快照（包含 Id、名称、并发度、超时设置等）
foreach (var snapshot in manager.GetSnapshot())
{
    Console.WriteLine($"Worker {snapshot.Id} Active={snapshot.IsActive} Current={snapshot.CurrentConcurrency}/{snapshot.MaxConcurrency}");
}
```

## ⚠️ 注意事项

1. **必须确认消息**：未确认的消息会一直占用 Worker 槽位，导致系统阻塞
2. **避免重复确认**：同一消息多次确认会抛出异常
3. **Prefetch 与内存**：Prefetch 越大，内存占用越多，需根据实际情况调整
4. **并发与资源**：ConcurrencyLimit 决定了资源消耗，需要权衡性能与资源
5. **超时设置**：HandlerTimeout 应根据实际业务处理时间合理设置
6. **SubscribeAsync 泛型**：订阅时需要显式指定消息类型，例如 `SubscribeAsync<int>(...)`

## 🧪 测试

仓库包含覆盖常见场景的单元测试（外部 ACK、Prefetch 限制、指标曝光等），运行方式：

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

