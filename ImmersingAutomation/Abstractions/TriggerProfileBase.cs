namespace ImmersingAutomation.Abstractions;

/// <summary>
/// 触发器的可序列化配置的基类。
/// </summary>
/// <remarks>
/// 从此类派生以描述触发器所需的数据，然后使用
/// <see cref="ImmersingAutomation.Attributes.TriggerProfileAttribute"/> 将配置关联到对应的触发器。
/// </remarks>
public abstract class TriggerProfileBase;
