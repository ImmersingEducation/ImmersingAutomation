namespace ImmersingAutomation.Enums;

/// <summary>
/// 描述 <see cref="ImmersingAutomation.Models.WorkflowRunner"/> 的生命周期状态。
/// </summary>
public enum WorkflowRunnerStatus
{
    /// <summary>运行器正在根据工作流配置创建触发器、规则集和动作。</summary>
    Initializing,

    /// <summary>运行器的触发器正在初始化。</summary>
    ExternalInitializing,

    /// <summary>运行器空闲，正在等待触发。</summary>
    WaitingForTriggering,

    /// <summary>运行器正在验证规则集。</summary>
    VerifyingRuleset,

    /// <summary>运行器正在执行动作。</summary>
    Running,

    /// <summary>已请求中断，正在处理。</summary>
    Interrupting,

    /// <summary>动作已执行完毕，运行器正在等待回滚条件。</summary>
    WaitingForReversion,

    /// <summary>运行器正在回滚动作。</summary>
    Reverting,

    /// <summary>运行器正在销毁触发器和动作。</summary>
    Destroying
}
