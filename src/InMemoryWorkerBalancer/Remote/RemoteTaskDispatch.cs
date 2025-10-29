using System.Threading;

namespace InMemoryWorkerBalancer.Remote;

/// <summary>
/// 表示一次待发送到远程 Worker 的分发请求。
/// </summary>
public readonly record struct RemoteTaskDispatch(RemoteWorkerSession Session, RemoteTaskEnvelope Envelope, CancellationToken CancellationToken);

