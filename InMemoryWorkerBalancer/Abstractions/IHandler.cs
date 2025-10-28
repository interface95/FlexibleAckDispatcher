using System.Threading;
using System.Threading.Tasks;

namespace WorkerBalancer.Abstractions;

/// <summary>
/// 统一的处理程序接口，外部实现后可被订阅注册使用。
/// </summary>
public interface IWorkerMessageHandler<T>
{
    /// <summary>
    /// 处理一条消息。
    /// </summary>
    Task HandleAsync(WorkerMessage<T> message, CancellationToken cancellationToken);
}
