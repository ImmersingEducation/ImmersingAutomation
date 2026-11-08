namespace ImmersingAutomation.Enums;

/// <summary>
/// 决定如何组合 <see cref="ImmersingAutomation.Models.Ruleset"/> 中的规则。
/// </summary>
public enum RulesetMode
{
    /// <summary>所有规则都必须验证通过。</summary>
    All,

    /// <summary>至少有一条规则验证通过。</summary>
    Any
}
