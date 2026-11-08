using ImmersingAutomation.Enums;

namespace ImmersingAutomation.Events;

/// <summary>
/// 提供 <see cref="Models.WorkflowRunner"/> 状态变化后的新状态。
/// </summary>
public class WorkflowRunnerStatusChangedEventArgs : EventArgs
{
    /// <summary>
    /// 获取工作流运行器变更后的状态。
    /// </summary>
    public WorkflowRunnerStatus Status { get; }

    /// <summary>
    /// 初始化 <see cref="WorkflowRunnerStatusChangedEventArgs"/> 类的新实例。
    /// </summary>
    /// <param name="status">工作流运行器变更后的状态。</param>
    public WorkflowRunnerStatusChangedEventArgs(WorkflowRunnerStatus status)
    {
        Status = status;
    }
}
