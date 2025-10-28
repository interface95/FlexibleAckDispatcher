# FlexibleAckDispatcher

一个高性能、灵活的内存消息调度系统，基于 .NET 实现的发布-订阅模式（Pub/Sub），支持灵活的消息确认机制、并发控制和负载均衡。

## 🌟 核心特性

- ✅ **灵活的 ACK 机制**：支持在处理函数内部或外部手动确认消息，类似 RabbitMQ 的模式
- 🚀 **高性能异步处理**：基于 `System.Threading.Channels` 实现的高效消息队列
- 🔄 **智能负载均衡**：多订阅者自动分发消息，实现 Worker 级别的负载均衡与连接池
- 🎯 **细粒度并发控制**：支持 Prefetch（预取数量）和 ConcurrencyLimit（并发限制）双重控制
- ⏱️ **超时保护机制**：内置消息处理超时，自动释放 Worker 槽位，防止阻塞
- 📊 **丰富运行时指标**：实时监控订阅者数量、空闲 Worker 数、执行中任务数以及 Worker 快照
- 🔌 **动态热插拔**：支持运行时动态添加和移除订阅者
- 🛡️ **失败保护**：连续失败阈值，达到限制后自动停止 Worker，防止级联故障

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
<PackageReference Include="InMemoryWorkerBalancer" Version="1.0.0" />
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
│       │   ├── IPubSubSubscription.cs     # 订阅句柄接口
│       │   └── IWorkerPayloadSerializer.cs # 负载序列化器接口
│       ├── Internal/                      # 内部实现
│       │   ├── WorkerDispatcher.cs        # 消息调度器
│       │   ├── WorkerEndpoint.cs         # Worker 端点
│       │   ├── WorkerManager.cs          # Worker 管理器
│       │   ├── WorkerProcessor.cs         # Worker 处理器
│       │   ├── WorkerDeliveryContext.cs  # 消息传递上下文
│       │   ├── WorkerCapacity.cs         # Worker 容量控制
│       │   ├── WorkerAckToken.cs         # ACK 令牌
│       │   ├── PubSubSubscription.cs      # 订阅实现
│       │   └── SnowflakeIdGenerator.cs    # 雪花 ID 生成器
│       ├── JsonWorkerPayloadSerializer.cs # 默认 JSON 序列化器
│       ├── PubSubManager.cs               # 发布订阅管理器（核心）
│       ├── PubSubManagerOptions.cs        # 管理器配置选项
│       ├── SubscriptionOptions.cs         # 订阅配置选项
│       ├── WorkerEndpointSnapshot.cs      # Worker 快照结构
│       ├── WorkerMessage.cs               # 消息包装器
│       └── WorkerProcessingDelegate.cs    # 处理委托
└── Test/
    └── TestWorkerBalancerPubSub.cs       # 综合测试
```

## 🏗️ 架构设计

### 核心组件

- **PubSubManager**: 消息发布-订阅管理器，负责整体调度和资源管理
- **WorkerManager**: Worker 生命周期管理，维护 Worker 池和负载均衡
- **WorkerDispatcher**: 消息分发器，将消息路由到空闲的 Worker
- **WorkerProcessor**: Worker 处理器，从专属通道读取并处理消息
- **TypedPubSubChannel**: 类型化的发布通道，支持泛型消息

### 工作流程

1. **订阅阶段**：调用 `SubscribeAsync` 创建 Worker，注册到 WorkerManager
2. **发布阶段**：调用 `PublishAsync` 将消息序列化后写入主通道
3. **调度阶段**：WorkerDispatcher 从主通道读取消息，分发给空闲 Worker
4. **处理阶段**：WorkerProcessor 从 Worker 专属通道读取消息并处理
5. **确认阶段**：调用 `AckAsync` 释放 Worker 槽位，允许处理下一条消息

## 🚀 快速开始

### 1. 基本用法

```csharp
using InMemoryWorkerBalancer;

// 创建 PubSubManager，使用默认 JSON 序列化和 NullLogger
await using var manager = PubSubManager.Create();

// 订阅消息并立即 ACK
await manager.SubscribeAsync<int>(async (message, cancellationToken) =>
{
    Console.WriteLine($"处理消息: {message.Payload}");
    await message.AckAsync(); // 必须调用 ACK 以释放 Worker 槽位
});

// 发布消息
for (int i = 0; i < 10; i++)
{
    await manager.PublishAsync(i);
}

await Task.Delay(1000); // 等待处理完成
```

### 2. 配置管理器选项

```csharp
await using var manager = PubSubManager.Create(options => options
    .WithLogger(logger)                                    // 配置日志记录器
    .WithSerializer(customSerializer)                      // 配置自定义序列化器
    .OnWorkerAddedHandler(async snapshot =>                // Worker 添加事件
    {
        Console.WriteLine($"Worker {snapshot.Id} ({snapshot.Name}) 已加入");
    })
    .OnWorkerRemovedHandler(async snapshot =>              // Worker 移除事件
    {
        Console.WriteLine($"Worker {snapshot.Id} 已离开");
    }));
```

### 3. 配置订阅选项

```csharp
await manager.SubscribeAsync<int>(
    async (message, cancellationToken) =>
    {
        Console.WriteLine($"Worker {message.WorkerId} 处理: {message.Payload}");
        await Task.Delay(100, cancellationToken);
        await message.AckAsync();
    },
    options => options
        .WithName("OrderProcessor")           // Worker 名称
        .WithPrefetch(10)                     // 预取 10 条消息
        .WithConcurrencyLimit(5)              // 最大并发 5
        .WithHandlerTimeout(TimeSpan.FromSeconds(30))  // 30秒超时
        .WithFailureThreshold(3));           // 3次失败后停止
```

**参数说明：**
- **Prefetch**: Worker 专属通道的容量，控制预取消息数量
- **ConcurrencyLimit**: Worker 内部最大并发任务数（≤ Prefetch）
- **HandlerTimeout**: 单条消息的最大处理时间
- **FailureThreshold**: Worker 连续失败次数阈值

### 4. 外部手动 ACK（类似 RabbitMQ）

```csharp
var deliveryTags = new List<long>();

await manager.SubscribeAsync<int>(async (message, cancellationToken) =>
{
    // 保存 deliveryTag，不立即 ACK
    deliveryTags.Add(message.DeliveryTag);
    Console.WriteLine($"收到消息: {message.Payload}，deliveryTag: {message.DeliveryTag}");
    // 注意：不在此处调用 AckAsync()
});

await manager.PublishAsync(42);
await Task.Delay(100); // 等待消息被投递

// 稍后在外部确认
if (deliveryTags.Count > 0)
{
    await manager.AckAsync(deliveryTags[0]);
}
```

### 5. 多订阅者负载均衡

```csharp
// 创建 3 个订阅者，消息将自动负载均衡
for (int i = 0; i < 3; i++)
{
    int workerIndex = i;
    await manager.SubscribeAsync<int>(
        async (message, cancellationToken) =>
        {
            Console.WriteLine($"订阅者 {workerIndex} (WorkerId={message.WorkerId}) 处理: {message.Payload}");
            await Task.Delay(100, cancellationToken);
            await message.AckAsync();
        },
        options => options
            .WithName($"Worker-{i}")
            .WithPrefetch(5));
}

// 发布 30 条消息，将自动分发到 3 个订阅者
for (int i = 0; i < 30; i++)
{
    await manager.PublishAsync(i);
}
```

### 6. 使用接口方式（推荐用于复杂业务）

```csharp
using InMemoryWorkerBalancer.Abstractions;

public class OrderMessageHandler : IWorkerMessageHandler<int>
{
    public async Task HandleAsync(WorkerMessage<int> message, CancellationToken cancellationToken)
    {
        Console.WriteLine($"Worker {message.WorkerId} 处理订单: {message.Payload}");
        await ProcessOrderAsync(message.Payload, cancellationToken);
        await message.AckAsync(); // 处理完成后确认
    }

    private async Task ProcessOrderAsync(int orderId, CancellationToken ct)
    {
        // 业务逻辑
        await Task.Delay(100, ct);
    }
}

var handler = new OrderMessageHandler();
await manager.SubscribeAsync<int>(handler, options => options.WithPrefetch(5));
```

### 7. 动态订阅与取消订阅

```csharp
// 添加订阅
var subscription1 = await manager.SubscribeAsync<int>(
    async (message, ct) => 
    {
        Console.WriteLine($"Worker {message.WorkerId}: {message.Payload}");
        await message.AckAsync();
    },
    options => options.WithName("DynamicWorker").WithPrefetch(3));

await Task.Delay(1000);

// 发布一些消息
for (int i = 0; i < 10; i++)
{
    await manager.PublishAsync(i);
}

// 取消订阅
await subscription1.DisposeAsync();
Console.WriteLine($"剩余订阅者数: {manager.SubscriberCount}");
```

## 📊 运行时观测与监控

### 获取基本指标

```csharp
// 订阅者数量
Console.WriteLine($"当前订阅者数: {manager.SubscriberCount}");

// 空闲 Worker 数（可用于负载判断）
Console.WriteLine($"空闲 Worker 数: {manager.IdleWorkerCount}");

// 正在处理的任务总数（所有 Worker 总和）
Console.WriteLine($"正在处理任务数: {manager.RunningTaskCount}");

// 获取所有 Worker 的详细快照
var snapshots = manager.GetSnapshot();
foreach (var snapshot in snapshots)
{
    Console.WriteLine(
        $"Worker {snapshot.Id} ({snapshot.Name ?? "未命名"}): " +
        $"Active={snapshot.IsActive}, " +
        $"Concurrent={snapshot.CurrentConcurrency}/{snapshot.MaxConcurrency}, " +
        $"Timeout={snapshot.HandlerTimeout}, " +
        $"Fault={snapshot.Fault?.Message ?? "None"}");
}
```

### 实现健康检查

```csharp
public class HealthCheck
{
    public static async Task<bool> CheckHealthAsync(IPubSubManager manager)
    {
        var snapshots = manager.GetSnapshot();
        
        // 检查所有 Worker 是否活跃
        foreach (var snapshot in snapshots)
        {
            if (!snapshot.IsActive || snapshot.Fault != null)
            {
                Console.WriteLine($"警告: Worker {snapshot.Id} 状态异常");
                return false;
            }
        }
        
        // 检查是否有空闲 Worker
        if (manager.IdleWorkerCount == 0 && manager.RunningTaskCount > 0)
        {
            Console.WriteLine("警告: 所有 Worker 都在忙，可能处理能力不足");
            return false;
        }
        
        return true;
    }
}

// 使用示例
var isHealthy = await HealthCheck.CheckHealthAsync(manager);
Console.WriteLine($"系统健康状态: {(isHealthy ? "正常" : "异常")}");
```

## ⚠️ 重要注意事项

### 1. 消息确认是必需的

⚠️ **未确认的消息会一直占用 Worker 槽位，导致系统阻塞！**

```csharp
// ❌ 错误示例：忘记 ACK，导致 Worker 阻塞
await manager.SubscribeAsync<int>(async (message, ct) =>
{
    Console.WriteLine($"处理: {message.Payload}");
    // 忘记调用 message.AckAsync()！
});

// ✅ 正确示例：及时 ACK
await manager.SubscribeAsync<int>(async (message, ct) =>
{
    Console.WriteLine($"处理: {message.Payload}");
    await message.AckAsync(); // 必须调用
});
```

### 2. 避免重复确认

```csharp
var deliveryTag = 0L;

await manager.SubscribeAsync<int>(async (message, ct) =>
{
    deliveryTag = message.DeliveryTag;
    await message.AckAsync();
});

await manager.PublishAsync(1);
await Task.Delay(100);

// ✅ 第一次确认 - 成功
await manager.AckAsync(deliveryTag);

// ❌ 第二次确认 - 会抛出异常
try
{
    await manager.AckAsync(deliveryTag); // InvalidOperationException
}
catch (InvalidOperationException ex)
{
    Console.WriteLine(ex.Message); // "未找到待确认的消息"
}
```

### 3. Prefetch 与内存权衡

```csharp
// Prefetch 越大，内存占用越多，但处理延迟可能降低
// 建议：根据实际消息大小和处理时间调整

// 小消息，快速处理
options => options.WithPrefetch(100)

// 大消息，慢速处理
options => options.WithPrefetch(5)
```

### 4. 并发限制与资源消耗

```csharp
// ConcurrencyLimit 控制 Worker 内部的并发任务数
// 过高可能导致资源耗尽，过低可能导致吞吐量不足

// 推荐：根据 CPU 核心数和 I/O 特点设置
options => options
    .WithPrefetch(10)
    .WithConcurrencyLimit(4)  // CPU 密集型设为 CPU 核心数
                              // I/O 密集型可以设更高
```

### 5. 超时设置要合理

```csharp
// 超时时间应略大于实际平均处理时间
options => options
    .WithHandlerTimeout(TimeSpan.FromSeconds(30)) // 根据业务调整
```

### 6. 泛型类型必须明确

```csharp
// ❌ 错误：无法推断类型
await manager.SubscribeAsync(async (msg, ct) => { });

// ✅ 正确：显式指定类型
await manager.SubscribeAsync<int>(async (msg, ct) => { });
await manager.SubscribeAsync<string>(async (msg, ct) => { });
await manager.SubscribeAsync<Order>(async (msg, ct) => { });
```

### 7. 正确处理异常

```csharp
await manager.SubscribeAsync<int>(async (message, cancellationToken) =>
{
    try
    {
        await ProcessMessageAsync(message.Payload, cancellationToken);
        await message.AckAsync();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"处理失败: {ex.Message}");
        // 不调用 Ack，消息会被重新投递或记录到失败队列
    }
});
```

### 8. 使用 CancellationToken

```csharp
await manager.SubscribeAsync<int>(async (message, cancellationToken) =>
{
    // 始终检查取消令牌
    cancellationToken.ThrowIfCancellationRequested();
    
    await ProcessAsync(message.Payload, cancellationToken);
    await message.AckAsync();
});
```

## 🧪 测试

仓库包含覆盖常见场景的综合单元测试，包括：

- ✅ 基本消息处理
- ✅ 外部手动 ACK
- ✅ Prefetch 限制验证
- ✅ 并发控制验证
- ✅ 超时保护验证
- ✅ 多订阅者负载均衡
- ✅ 动态订阅管理
- ✅ 运行时指标监控

运行测试：

```bash
dotnet test
```

## 📊 性能特点

### 核心性能优化

1. **零拷贝设计**
   - 使用 `ReadOnlyMemory<byte>` 传递消息，避免不必要的内存拷贝
   - 基于 `System.Threading.Channels` 实现高效的消息队列

2. **无锁并发**
   - 核心路径使用 `ConcurrentDictionary` 和 `Channel`
   - 减少锁竞争，提升高并发场景性能

3. **异步优先**
   - 全异步 API，充分利用 .NET 异步机制
   - 异步发布、订阅、处理，不阻塞线程池

4. **内存高效**
   - 使用对象池和可重用结构减少 GC 压力
   - 延迟分配，按需创建对象

### 性能指标参考

- 支持数万级别的消息/秒吞吐量
- 极低的处理延迟（微秒级）
- 内存占用与 Prefetch 成正比
- 线性扩展性（多订阅者场景）

### 适用场景

✅ **推荐使用**
- 内存消息队列
- 事件驱动架构
- 微服务内部通信
- 高并发异步任务分发
- 需要灵活 ACK 的短时间任务处理

❌ **不适用**
- 持久化存储（纯内存，进程退出后丢失）
- 跨进程/网络通信（需配合消息队列中间件）
- 超长时间任务（建议使用专门的作业调度系统）

## 🛠️ 技术栈

- **.NET 8.0 / 9.0** - 跨平台支持
- **C# 12** - 最新语言特性
- **System.Threading.Channels** - 高性能异步队列
- **Microsoft.Extensions.Logging.Abstractions** - 日志抽象

## 📚 更多资源

### 相关链接

- [GitHub Repository](https://github.com/interface95/FlexibleAckDispatcher)
- [Issue Tracker](https://github.com/interface95/FlexibleAckDispatcher/issues)
- [NuGet Package](https://www.nuget.org/packages/InMemoryWorkerBalancer)

### 使用建议

1. **小消息场景**：Prefetch 可设较大值（50-100），提升吞吐量
2. **大消息场景**：Prefetch 设较小值（5-10），控制内存占用
3. **I/O 密集型**：ConcurrencyLimit 可设较大值（10-50）
4. **CPU 密集型**：ConcurrencyLimit 应等于或略大于 CPU 核心数
5. **混合场景**：根据实际业务情况调整参数

## 📄 许可证

本项目采用 MIT 许可证。

## 🤝 贡献

欢迎提交 Issue 和 Pull Request！

### 贡献指南

1. Fork 项目
2. 创建特性分支 (`git checkout -b feature/AmazingFeature`)
3. 提交更改 (`git commit -m 'Add some AmazingFeature'`)
4. 推送到分支 (`git push origin feature/AmazingFeature`)
5. 开启 Pull Request

---

**项目作者**: FlexibleAckDispatcher Team  
**当前版本**: 1.0.0  
**最后更新**: 2025-10-28

