namespace ImmersingAutomation.Attributes;

/// <summary>
/// 标记动作实现，并指定用于持久化的标识符。
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public class ActionAttribute : Attribute
{
    /// <summary>获取动作的唯一标识符。</summary>
    public Guid Guid { get; }

    /// <summary>获取动作的显示名称。</summary>
    public string Name { get; }

    /// <summary>获取动作是否支持回滚。</summary>
    public bool IsReversionSupported { get; }

    /// <summary>
    /// 初始化 <see cref="ActionAttribute"/> 类的新实例。
    /// </summary>
    /// <param name="guid">动作的唯一标识符。</param>
    /// <param name="name">动作的显示名称。</param>
    /// <param name="isReversionSupported">动作是否支持回滚。</param>
    public ActionAttribute(Guid guid, string name, bool isReversionSupported)
    {
        Guid = guid;
        Name = name;
        IsReversionSupported = isReversionSupported;
    }

    /// <summary>
    /// 初始化 <see cref="ActionAttribute"/> 类的新实例。
    /// </summary>
    /// <param name="guid">GUID 字符串格式的动作唯一标识符。</param>
    /// <param name="name">动作的显示名称。</param>
    /// <param name="isReversionSupported">动作是否支持回滚。</param>
    public ActionAttribute(string guid, string name, bool isReversionSupported)
        : this(Guid.Parse(guid), name, isReversionSupported)
    {
    }
}
