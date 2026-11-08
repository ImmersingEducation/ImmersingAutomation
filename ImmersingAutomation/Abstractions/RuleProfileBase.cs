namespace ImmersingAutomation.Abstractions;

/// <summary>
/// 规则的可序列化配置的基类。
/// </summary>
/// <remarks>
/// 从此类派生以描述规则所需的数据，然后使用
/// <see cref="ImmersingAutomation.Attributes.RuleProfileAttribute"/> 将配置关联到对应的规则。
/// </remarks>
public abstract class RuleProfileBase;
