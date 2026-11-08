using ImmersingAutomation.Abstractions;
using ImmersingAutomation.Enums;

namespace ImmersingAutomation.Models;

public class RulesetProfile : RuleProfileBase
{
    public List<RuleBase> Rules { get; set; }
    
    public RulesetMode Mode { get; set; }
}