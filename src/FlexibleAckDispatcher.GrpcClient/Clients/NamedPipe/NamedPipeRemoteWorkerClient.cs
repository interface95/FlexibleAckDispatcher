using System;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using GrpcDotNetNamedPipes;
using FlexibleAckDispatcher.GrpcClient.Clients.NamedPipe.Options;
using FlexibleAckDispatcher.GrpcServer.Protos;

namespace FlexibleAckDispatcher.GrpcClient.Clients.NamedPipe;

/// <summary>
/// 基于 <see cref="NamedPipeChannel"/> 的远程 Worker 客户端封装。
/// </summary>
public sealed class NamedPipeRemoteWorkerClient : IAsyncDisposable
{
    private readonly NamedPipeRemoteWorkerClientOptions _options;
    private readonly WorkerHub.WorkerHubClient _client;
    private readonly TimeSpan _heartbeatInterval;
    private readonly bool _autoHeartbeatEnabled;

    private CancellationTokenSource? _heartbeatCts;
    private Task? _heartbeatTask;
    private int _registeredWorkerId;

    private NamedPipeRemoteWorkerClient(NamedPipeRemoteWorkerClientOptions options)
    {
        _options = options;
        var channel = new NamedPipeChannel(options.ServerName, options.PipeName);
        _client = new WorkerHub.WorkerHubClient(channel);
        _autoHeartbeatEnabled = options.AutoHeartbeatEnabled;
        _heartbeatInterval = options.HeartbeatInterval;
    }

    /// <summary>
    /// 创建一个基于命名管道的远程 Worker 客户端。
    /// </summary>
    /// <param name="configure">配置命名管道连接选项的委托。</param>
    /// <returns>可用于与主进程通信的客户端实例。</returns>
    public static NamedPipeRemoteWorkerClient Create(Action<NamedPipeRemoteWorkerClientOptions>? configure = null)
    {
        var options = new NamedPipeRemoteWorkerClientOptions();
        configure?.Invoke(options);
        options.Validate();
        return new NamedPipeRemoteWorkerClient(options);
    }

    /// <summary>
    /// 注册远程 Worker，并获取分配的 WorkerId。
    /// </summary>
    /// <param name="configure">配置注册请求的委托。</param>
    /// <param name="cancellationToken">取消操作。</param>
    /// <returns>注册回复，其中包含 WorkerId。</returns>
    public async Task<RegisterReply> RegisterAsync(Action<RegisterRequestOptions> configure, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var options = new RegisterRequestOptions();
        configure(options);
        options.Validate();
        var request = options.ToRequest();
        var reply = await _client.RegisterAsync(request, cancellationToken: cancellationToken).ResponseAsync.ConfigureAwait(false);

        if (_autoHeartbeatEnabled)
        {
            StartAutoHeartbeat(reply.WorkerId);
        }

        return reply;
    }

    /// <summary>
    /// 订阅主进程下发的任务流。
    /// </summary>
    /// <param name="configure">配置订阅请求的委托。</param>
    /// <param name="cancellationToken">取消操作。</param>
    /// <returns>可用于读取任务的服务器流。</returns>
    public AsyncServerStreamingCall<DispatcherMessage> SubscribeAsync(Action<SubscribeRequestOptions> configure, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var options = new SubscribeRequestOptions();
        configure(options);
        options.Validate();
        var request = options.ToRequest();
        return _client.Subscribe(request, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// 向主进程发送 ACK，确认任务处理完成。
    /// </summary>
    /// <param name="configure">配置 ACK 请求的委托。</param>
    /// <param name="cancellationToken">取消操作。</param>
    public Task AckAsync(Action<AckRequestOptions> configure, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var options = new AckRequestOptions();
        configure(options);
        options.Validate();
        var request = options.ToRequest();
        return _client.AckAsync(request, cancellationToken: cancellationToken).ResponseAsync;
    }

    /// <summary>
    /// 向主进程发送 NACK，通知任务处理失败或需要重试。
    /// </summary>
    /// <param name="configure">配置 NACK 请求的委托。</param>
    /// <param name="cancellationToken">取消操作。</param>
    public Task NackAsync(Action<NackRequestOptions> configure, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var options = new NackRequestOptions();
        configure(options);
        options.Validate();
        var request = options.ToRequest();
        return _client.NackAsync(request, cancellationToken: cancellationToken).ResponseAsync;
    }

    /// <summary>
    /// 向主进程发送心跳，维持会话存活。
    /// </summary>
    /// <param name="configure">配置心跳请求的委托。</param>
    /// <param name="cancellationToken">取消操作。</param>
    public Task HeartbeatAsync(Action<HeartbeatRequestOptions> configure, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var options = new HeartbeatRequestOptions();
        configure(options);
        options.Validate();
        var request = options.ToRequest();
        return _client.HeartbeatAsync(request, cancellationToken: cancellationToken).ResponseAsync;
    }

    /// <summary>
    /// 异步释放客户端资源。
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await StopAutoHeartbeatAsync().ConfigureAwait(false);
    }

    private void StartAutoHeartbeat(int workerId)
    {
        if (workerId <= 0)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _registeredWorkerId, workerId, 0) != 0)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        if (Interlocked.CompareExchange(ref _heartbeatCts, cts, null) != null)
        {
            cts.Dispose();
            return;
        }

        _heartbeatTask = Task.Run(() => HeartbeatPumpAsync(workerId, cts.Token), cts.Token);
    }

    private async Task HeartbeatPumpAsync(int workerId, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var request = new HeartbeatRequest
                {
                    WorkerId = workerId,
                    Ticks = DateTimeOffset.UtcNow.Ticks
                };

                await _client.HeartbeatAsync(request, cancellationToken: token).ResponseAsync.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled && token.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // 忽略瞬时异常，留待下次心跳重试。
            }

            try
            {
                await Task.Delay(_heartbeatInterval, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async ValueTask StopAutoHeartbeatAsync()
    {
        var cts = Interlocked.Exchange(ref _heartbeatCts, null);
        if (cts is null)
        {
            return;
        }

        cts.Cancel();

        try
        {
            if (_heartbeatTask is not null)
            {
                await _heartbeatTask.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            cts.Dispose();
            _heartbeatTask = null;
            Interlocked.Exchange(ref _registeredWorkerId, 0);
        }
    }
}
