using ImmersingAutomation.Abstractions;

namespace ImmersingAutomation.Attributes;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public class ActionProfileAttribute : Attribute
{
    public Type ActionType { get; }

    public ActionProfileAttribute(Type actionType)
    {
        ArgumentNullException.ThrowIfNull(actionType);

        if (!typeof(ActionBase).IsAssignableFrom(actionType))
        {
            throw new ArgumentException(
                $"The action type must derive from '{typeof(ActionBase).FullName}'.",
                nameof(actionType));
        }

        ActionType = actionType;
    }
}
