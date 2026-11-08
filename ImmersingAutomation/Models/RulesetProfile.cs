using ImmersingAutomation.Abstractions;
using ImmersingAutomation.Enums;

namespace ImmersingAutomation.Models;

/// <summary>
/// 描述 <see cref="Ruleset"/> 的规则及其组合方式。
/// </summary>
public class RulesetProfile : RuleProfileBase
{
    /// <summary>
    /// 获取或设置构成规则集的规则。
    /// </summary>
    public List<RuleBase> Rules { get; set; } = [];

    /// <summary>
    /// 获取或设置规则的组合方式。
    /// </summary>
    public RulesetMode Mode { get; set; }
}
