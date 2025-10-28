using System.Threading.Channels;
using InMemoryWorkerBalancer.Internal;

namespace InMemoryWorkerBalancer;

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
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (!workerManager.TryRentAvailableWorker(out var endpoint))
                    {
                        await workerManager.WaitForWorkerAvailableAsync(cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    if (!endpoint.IsActive)
                    {
                        continue;
                    }

                    try
                    {
                        await endpoint.Writer.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
                        break;
                    }
                    catch (ChannelClosedException)
                    {
                        // Worker was removed after renting; retry with another worker.
                        continue;
                    }
                }
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

