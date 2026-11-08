using ImmersingAutomation.Abstractions;
using ImmersingAutomation.Enums;

namespace ImmersingAutomation.Models;

public class Workflow
{
    public Guid Guid { get; init; } = Guid.NewGuid();
    
    public string Name { get; set; }

    public bool IsEnabled
    {
        get;
        set
        {
            field = value;
            IsEnabledChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public List<TriggerProfileBase> TriggerProfiles { get; set; }
    
    public RulesetProfile RulesetProfile { get; set; }
    
    public List<ActionProfileBase> ActionProfiles { get; set; }
    
    public bool IsReversionEnabled { get; set; }
    
    public WorkflowRunnerExceptionHandleMode ExceptionHandleMode { get; set; }

    public event EventHandler? IsEnabledChanged;
}