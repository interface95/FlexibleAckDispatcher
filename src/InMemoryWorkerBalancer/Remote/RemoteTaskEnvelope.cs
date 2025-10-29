using System;
using System.Collections.Generic;

namespace InMemoryWorkerBalancer.Remote;

/// <summary>
/// 远程分发任务的载体，包含消息负载及上下文信息。
/// </summary>
public readonly record struct RemoteTaskEnvelope(
    long DeliveryTag,
    string MessageType,
    ReadOnlyMemory<byte> Payload,
    IReadOnlyDictionary<string, string>? Headers);

