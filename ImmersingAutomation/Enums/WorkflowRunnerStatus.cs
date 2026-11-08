namespace ImmersingAutomation.Enums;

public enum WorkflowRunnerStatus
{
    Initializing,
    ExternalInitializing,
    WaitingForTriggering,
    VerifyingRuleset,
    Running,
    Interrupting,
    WaitingForReversion,
    Reverting,
    Destroying
}