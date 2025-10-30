using System;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using GrpcDotNetNamedPipes;
using FlexibleAckDispatcher.GrpcClient.Clients.NamedPipe.Options;
using InMemoryWorkerBalancer.Remote.Protos;

namespace FlexibleAckDispatcher.GrpcClient.Clients.NamedPipe;

/// <summary>
/// 基于 <see cref="NamedPipeChannel"/> 的远程 Worker 客户端封装。
/// </summary>
public sealed class NamedPipeRemoteWorkerClient : IAsyncDisposable
{
    private readonly NamedPipeRemoteWorkerClientOptions _options;
    private readonly WorkerHub.WorkerHubClient _client;

    private NamedPipeRemoteWorkerClient(NamedPipeRemoteWorkerClientOptions options)
    {
        _options = options;
        var channel = new NamedPipeChannel(options.ServerName, options.PipeName);
        _client = new WorkerHub.WorkerHubClient(channel);
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
    public Task<RegisterReply> RegisterAsync(Action<RegisterRequestOptions> configure, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var options = new RegisterRequestOptions();
        configure(options);
        options.Validate();
        var request = options.ToRequest();
        return _client.RegisterAsync(request, cancellationToken: cancellationToken).ResponseAsync;
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
    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}
