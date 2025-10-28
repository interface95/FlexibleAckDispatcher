using System.Threading;

namespace InMemoryWorkerBalancer.Internal;

/// <summary>
/// 简易的自增 ID 生成器，用于生产全局唯一的 deliveryTag。
/// </summary>
internal static class SnowflakeIdGenerator
{
    private static long _current;

    /// <summary>
    /// 获取一个新的全局唯一 ID。
    /// </summary>
    public static long NextId()
    {
        while (true)
        {
            var current = Volatile.Read(ref _current);
            var next = current == long.MaxValue ? 1 : current + 1;
            if (Interlocked.CompareExchange(ref _current, next, current) == current)
            {
                return next;
            }
        }
    }
}

