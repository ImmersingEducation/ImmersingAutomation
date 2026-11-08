namespace ImmersingAutomation.Attributes;

/// <summary>
/// 标记触发器实现，并指定用于持久化的标识符。
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public class TriggerAttribute : Attribute
{
    /// <summary>获取触发器的唯一标识符。</summary>
    public Guid Guid { get; }

    /// <summary>获取触发器的显示名称。</summary>
    public string Name { get; }

    /// <summary>
    /// 初始化 <see cref="TriggerAttribute"/> 类的新实例。
    /// </summary>
    /// <param name="guid">触发器的唯一标识符。</param>
    /// <param name="name">触发器的显示名称。</param>
    public TriggerAttribute(Guid guid, string name)
    {
        Guid = guid;
        Name = name;
    }

    /// <summary>
    /// 初始化 <see cref="TriggerAttribute"/> 类的新实例。
    /// </summary>
    /// <param name="guid">GUID 字符串格式的触发器唯一标识符。</param>
    /// <param name="name">触发器的显示名称。</param>
    public TriggerAttribute(string guid, string name) : this(Guid.Parse(guid), name)
    {
    }
}
