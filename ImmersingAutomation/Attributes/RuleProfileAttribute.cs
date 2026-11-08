using ImmersingAutomation.Abstractions;

namespace ImmersingAutomation.Attributes;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public class RuleProfileAttribute : Attribute
{
    public Type RuleType { get; }

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
