using ImmersingAutomation.Abstractions;
using ImmersingAutomation.Enums;

namespace ImmersingAutomation.Models;

public class Ruleset : RuleBase<RulesetProfile>
{
    protected override async Task<bool> _ValidateAsync()
    {
        var rules = Profile.Rules ?? [];

        if (Profile.Mode is RulesetMode.All)
        {
            foreach (var rule in rules)
            {
                if (!await rule.ValidateAsync())
                {
                    return false;
                }
            }

            return true;
        }

        foreach (var rule in rules)
        {
            if (await rule.ValidateAsync())
            {
                return true;
            }
        }

        return false;
    }
}