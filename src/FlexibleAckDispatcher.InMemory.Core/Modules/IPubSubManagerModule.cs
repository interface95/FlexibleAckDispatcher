namespace FlexibleAckDispatcher.InMemory.Core.Modules;

/// <summary>
/// 扩展模块，可在构建 <see cref="PubSubManager"/> 时注入额外能力。
/// </summary>
public interface IPubSubManagerModule
{
    /// <summary>
    /// 将模块挂载到指定的 <see cref="PubSubManagerOptions"/>。
    /// </summary>
    /// <param name="options">用于配置管理器的选项。</param>
    void Configure(PubSubManagerOptions options);
}
