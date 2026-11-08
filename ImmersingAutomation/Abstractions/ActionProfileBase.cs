namespace ImmersingAutomation.Abstractions;

/// <summary>
/// 动作的可序列化配置的基类。
/// </summary>
/// <remarks>
/// 从此类派生以描述动作所需的数据，然后使用
/// <see cref="ImmersingAutomation.Attributes.ActionProfileAttribute"/> 将配置关联到对应的动作。
/// </remarks>
public abstract class ActionProfileBase;
