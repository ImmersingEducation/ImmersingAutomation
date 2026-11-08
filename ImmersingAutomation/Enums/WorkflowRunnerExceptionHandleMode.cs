namespace ImmersingAutomation.Enums;

/// <summary>
/// 决定 <see cref="ImmersingAutomation.Models.WorkflowRunner"/> 在动作失败时的处理方式。
/// </summary>
public enum WorkflowRunnerExceptionHandleMode
{
    /// <summary>跳过失败的动作，继续执行剩余动作。</summary>
    Skip,

    /// <summary>中断运行并回滚已执行的动作。</summary>
    Interrupt
}
