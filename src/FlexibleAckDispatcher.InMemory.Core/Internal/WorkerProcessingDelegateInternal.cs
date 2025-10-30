using System.Threading;
using System.Threading.Tasks;

namespace FlexibleAckDispatcher.InMemory.Core.Internal;

/// <summary>
/// 非泛型的内部处理委托，worker 运行时时使用。
/// </summary>
internal delegate Task WorkerProcessingDelegate(WorkerDeliveryContext message, CancellationToken cancellationToken);
