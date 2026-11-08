using ImmersingAutomation.Abstractions;

namespace ImmersingAutomation.Attributes;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public class TriggerProfileAttribute : Attribute
{
    public Type TriggerType { get; }

    public TriggerProfileAttribute(Type triggerType)
    {
        ArgumentNullException.ThrowIfNull(triggerType);

        if (!typeof(TriggerBase).IsAssignableFrom(triggerType))
        {
            throw new ArgumentException(
                $"The trigger type must derive from '{typeof(TriggerBase).FullName}'.",
                nameof(triggerType));
        }

        TriggerType = triggerType;
    }
}
