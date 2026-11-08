namespace ImmersingAutomation.Enums;

/// <summary>
/// 描述 <see cref="ImmersingAutomation.Abstractions.ActionBase"/> 的生命周期状态。
/// </summary>
public enum ActionStatus
{
    /// <summary>动作空闲，可以运行。</summary>
    WaitingForRunning,

    /// <summary>动作正在运行。</summary>
    Running,

    /// <summary>动作运行或回滚期间已请求中断。</summary>
    Interrupting,

    /// <summary>动作已运行，正在等待回滚。</summary>
    WaitingForReversion,

    /// <summary>动作正在回滚。</summary>
    Reverting
}
