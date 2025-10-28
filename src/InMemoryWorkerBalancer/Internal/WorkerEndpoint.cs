using System.Threading.Channels;

namespace InMemoryWorkerBalancer.Internal;

/// <summary>
/// 表示一个 Worker 的运行时上下文，包含专属队列、运行任务等信息。
/// </summary>
internal sealed class WorkerEndpoint
{
    private Task? _runningTask;

    public WorkerEndpoint(int id, Channel<ReadOnlyMemory<byte>> channel, CancellationTokenSource cancellation, WorkerCapacity capacity)
    {
        Id = id;
        Channel = channel;
        Cancellation = cancellation;
        Capacity = capacity;
    }

    /// <summary>
    /// Worker 的唯一标识，用于日志与匹配订阅。
    /// </summary>
    public int Id { get; }

    /// <summary>
    /// Worker 专属的消息通道，调度器往里面写入业务消息。
    /// </summary>
    internal Channel<ReadOnlyMemory<byte>> Channel { get; }

    /// <summary>
    /// 写端便捷访问器，调度器直接通过此 Writer 推送消息。
    /// </summary>
    internal ChannelWriter<ReadOnlyMemory<byte>> Writer => Channel.Writer;

    /// <summary>
    /// 控制 Worker 生命周期的取消源，移除或 Dispose 时会触发取消。
    /// </summary>
    internal CancellationTokenSource Cancellation { get; }

    /// <summary>
    /// Worker 的并发容量控制器，限制同一时间执行的消息数量。
    /// </summary>
    public WorkerCapacity Capacity { get; }

    /// <summary>
    /// Worker 是否仍处于活跃状态；调度器会过滤不活跃的 Worker。
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Worker 的友好名称，便于日志或诊断。
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Handler 执行的最大允许时长。
    /// </summary>
    public TimeSpan HandlerTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// 连续失败或超时的阈值，超过后会让 worker 停止工作并抛出异常。
    /// </summary>
    public int FailureThreshold { get; set; } = 3;

    /// <summary>
    /// 记录 Worker 正在运行的后台任务，以便停止时等待完成。
    /// </summary>
    /// <param name="runningTask">处理循环 Task。</param>
    internal void SetRunningTask(Task runningTask)
    {
        _runningTask = runningTask;
    }

    public Exception? Fault { get; private set; }

    /// <summary>
    /// 等待当前运行任务完成，忽略取消异常，用于优雅关闭。
    /// </summary>
    internal async Task WaitForCompletionAsync()
    {
        if (_runningTask is null)
        {
            return;
        }

        try
        {
            await _runningTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Fault = ex;
        }
    }

    internal WorkerEndpointSnapshot CreateSnapshot()
    {
        return new WorkerEndpointSnapshot(
            Id,
            Name,
            IsActive,
            Capacity.MaxConnections,
            Capacity.CurrentConnections,
            HandlerTimeout,
            FailureThreshold,
            Fault);
    }
}

