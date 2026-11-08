using ImmersingAutomation.Enums;

namespace ImmersingAutomation.Events;

public class WorkflowRunnerStatusChangedEventArgs : EventArgs
{
    public WorkflowRunnerStatus Status { get; }

    public WorkflowRunnerStatusChangedEventArgs(WorkflowRunnerStatus status)
    {
        Status = status;
    }
}
