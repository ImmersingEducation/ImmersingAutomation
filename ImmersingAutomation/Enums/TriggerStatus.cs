namespace ImmersingAutomation.Enums;

/// <summary>
/// 描述 <see cref="ImmersingAutomation.Abstractions.TriggerBase"/> 的生命周期状态。
/// </summary>
public enum TriggerStatus
{
    /// <summary>触发器已创建，但尚未初始化。</summary>
    Created,

    /// <summary>触发器正在初始化。</summary>
    Initializing,

    /// <summary>触发器已初始化，可以轮询。</summary>
    Ready,

    /// <summary>触发器正在释放。</summary>
    Releasing,

    /// <summary>触发器已释放，可以再次初始化。</summary>
    Released,

    /// <summary>最近一次初始化或释放失败。</summary>
    Faulted,

    /// <summary>触发器已销毁，不能再使用。</summary>
    Disposed
}
