using ImmersingAutomation.Abstractions;

namespace ImmersingAutomation.Models;

public class Workflow
{
    public Guid Guid { get; init; } = Guid.NewGuid();
    
    public string Name { get; set; }
    
    public bool IsEnabled { get; set; }
    
    public List<TriggerProfileBase> TriggerProfiles { get; set; }
    
    public RulesetProfile RulesetProfile { get; set; }
    
    public List<ActionProfileBase> ActionProfiles { get; set; }
    
    public bool IsReversionEnabled { get; set; }
}