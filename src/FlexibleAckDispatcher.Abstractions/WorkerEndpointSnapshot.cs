using System;

namespace FlexibleAckDispatcher;

/// <summary>
/// Worker 端点的只读快照信息。
/// </summary>
public readonly record struct WorkerEndpointSnapshot(
    int Id,
    string? Name,
    bool IsActive,
    int MaxConcurrency,
    int CurrentConcurrency,
    TimeSpan HandlerTimeout,
    int FailureThreshold,
    TimeSpan? AckTimeout,
    Exception? Fault);

