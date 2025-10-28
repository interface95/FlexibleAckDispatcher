using System.Threading;
using InMemoryWorkerBalancer.Internal;

namespace InMemoryWorkerBalancer;

/// <summary>
/// Worker 所需的处理委托，返回后表示处理完成。
/// </summary>
public delegate Task WorkerProcessingDelegate<T>(WorkerMessage<T> message, CancellationToken cancellationToken);

/// <summary>
/// 非泛型的内部处理委托，worker 运行时时使用。
/// </summary>
internal delegate Task WorkerProcessingDelegate(WorkerDeliveryContext message, CancellationToken cancellationToken);

