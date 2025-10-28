using System.Threading.Channels;
using WorkerBalancer.Internal;

namespace WorkerBalancer;

/// <summary>
/// 负责从主通道读取消息并分配给可用 Worker。
/// </summary>
public sealed class WorkerDispatcher<T>
{
    /// <summary>
    /// 调度循环，从 sourceReader 读取消息并写入 Worker 专属队列。
    /// </summary>
    public async Task ProcessAsync(
        ChannelReader<T> sourceReader,
        WorkerManager<T> workerManager,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var payload in sourceReader.ReadAllAsync(cancellationToken))
            {
                WorkerEndpoint<T> endpoint;
                while (!workerManager.TryRentAvailableWorker(out endpoint))
                {
                    await workerManager.WaitForWorkerAvailableAsync(cancellationToken);
                }

                await endpoint.Writer.WriteAsync(payload, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            workerManager.CompleteWriters();
        }
    }
}

