namespace ImmersingAutomation.Attributes;

/// <summary>
/// 标记规则实现，并指定用于持久化的标识符。
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public class RuleAttribute : Attribute
{
    /// <summary>获取规则的唯一标识符。</summary>
    public Guid Guid { get; }

    /// <summary>获取规则的显示名称。</summary>
    public string Name { get; }

    /// <summary>
    /// 初始化 <see cref="RuleAttribute"/> 类的新实例。
    /// </summary>
    /// <param name="guid">规则的唯一标识符。</param>
    /// <param name="name">规则的显示名称。</param>
    public RuleAttribute(Guid guid, string name)
    {
        Guid = guid;
        Name = name;
    }

    /// <summary>
    /// 初始化 <see cref="RuleAttribute"/> 类的新实例。
    /// </summary>
    /// <param name="guid">GUID 字符串格式的规则唯一标识符。</param>
    /// <param name="name">规则的显示名称。</param>
    public RuleAttribute(string guid, string name) : this(Guid.Parse(guid), name)
    {
    }
}
