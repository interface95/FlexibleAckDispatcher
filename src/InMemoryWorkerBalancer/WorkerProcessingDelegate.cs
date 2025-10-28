namespace InMemoryWorkerBalancer;

/// <summary>
/// Worker 所需的处理委托，返回后表示处理完成。
/// </summary>
public delegate Task WorkerProcessingDelegate<T>(WorkerMessage<T> message, CancellationToken cancellationToken);

