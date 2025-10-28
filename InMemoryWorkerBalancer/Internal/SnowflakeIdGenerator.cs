using System.Threading;

namespace WorkerBalancer.Internal;

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
        return Interlocked.Increment(ref _current);
    }
}

