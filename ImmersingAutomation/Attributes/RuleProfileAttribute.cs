using ImmersingAutomation.Abstractions;

namespace ImmersingAutomation.Attributes;

/// <summary>
/// 将 <see cref="RuleProfileBase"/> 关联到使用它的规则实现。
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public class RuleProfileAttribute : Attribute
{
    /// <summary>获取使用所标注配置的规则类型。</summary>
    public Type RuleType { get; }

    /// <summary>
    /// 初始化 <see cref="RuleProfileAttribute"/> 类的新实例。
    /// </summary>
    /// <param name="ruleType">使用所标注配置的规则类型。</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="ruleType"/> 不是从 <see cref="RuleBase"/> 派生。
    /// </exception>
    public RuleProfileAttribute(Type ruleType)
    {
        ArgumentNullException.ThrowIfNull(ruleType);

        if (!typeof(RuleBase).IsAssignableFrom(ruleType))
        {
            throw new ArgumentException(
                $"The rule type must derive from '{typeof(RuleBase).FullName}'.",
                nameof(ruleType));
        }

        RuleType = ruleType;
    }
}
