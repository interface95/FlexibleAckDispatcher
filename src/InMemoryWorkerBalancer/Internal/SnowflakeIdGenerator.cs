using System.Threading;

namespace InMemoryWorkerBalancer.Internal;

/// <summary>
/// 简易的自增 ID 生成器，用于生产全局唯一的 deliveryTag。
/// 保证生成的 ID 始终为正数，并在溢出时安全地回绕。
/// </summary>
internal static class SnowflakeIdGenerator
{
    private static long _current;

    /// <summary>
    /// 获取一个新的全局唯一 ID。
    /// </summary>
    public static long NextId()
    {
        var next = Interlocked.Increment(ref _current);
        if (next > 0)
        {
            return next;
        }

        // 溢出：将计数器重置为 1，避免返回非正数。
        lock (typeof(SnowflakeIdGenerator))
        {
            if (_current <= 0)
            {
                _current = 1;
                return 1;
            }

            return _current;
        }
    }
}

