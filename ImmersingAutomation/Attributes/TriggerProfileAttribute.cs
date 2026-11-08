using ImmersingAutomation.Abstractions;

namespace ImmersingAutomation.Attributes;

/// <summary>
/// 将 <see cref="TriggerProfileBase"/> 关联到使用它的触发器实现。
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public class TriggerProfileAttribute : Attribute
{
    /// <summary>获取使用所标注配置的触发器类型。</summary>
    public Type TriggerType { get; }

    /// <summary>
    /// 初始化 <see cref="TriggerProfileAttribute"/> 类的新实例。
    /// </summary>
    /// <param name="triggerType">使用所标注配置的触发器类型。</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="triggerType"/> 不是从 <see cref="TriggerBase"/> 派生。
    /// </exception>
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
