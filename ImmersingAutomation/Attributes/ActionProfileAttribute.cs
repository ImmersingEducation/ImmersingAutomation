using ImmersingAutomation.Abstractions;

namespace ImmersingAutomation.Attributes;

/// <summary>
/// 将 <see cref="ActionProfileBase"/> 关联到使用它的动作实现。
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public class ActionProfileAttribute : Attribute
{
    /// <summary>获取使用所标注配置的动作类型。</summary>
    public Type ActionType { get; }

    /// <summary>
    /// 初始化 <see cref="ActionProfileAttribute"/> 类的新实例。
    /// </summary>
    /// <param name="actionType">使用所标注配置的动作类型。</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="actionType"/> 不是从 <see cref="ActionBase"/> 派生。
    /// </exception>
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
