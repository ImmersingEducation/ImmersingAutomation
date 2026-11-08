using ImmersingAutomation.Abstractions;
using ImmersingAutomation.Enums;

namespace ImmersingAutomation.Models;

/// <summary>
/// 使用 <see cref="RulesetMode"/> 将多条规则组合成一个条件。
/// </summary>
public class Ruleset : RuleBase<RulesetProfile>
{
    /// <summary>
    /// 当 <see cref="RulesetProfile.Mode"/> 为 <see cref="RulesetMode.All"/> 时验证所有规则，
    /// 否则验证到有一条规则匹配为止。
    /// </summary>
    /// <param name="cancellationToken">用于取消操作的标记。</param>
    protected override async Task<bool> _ValidateAsync(CancellationToken cancellationToken)
    {
        var rules = Profile.Rules ?? [];

        if (Profile.Mode is RulesetMode.All)
        {
            foreach (var rule in rules)
            {
                if (!await rule.ValidateAsync(cancellationToken).ConfigureAwait(false))
                {
                    return false;
                }
            }

            return true;
        }

        foreach (var rule in rules)
        {
            if (await rule.ValidateAsync(cancellationToken).ConfigureAwait(false))
            {
                return true;
            }
        }

        return false;
    }
}
