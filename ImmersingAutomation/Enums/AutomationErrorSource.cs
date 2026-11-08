namespace ImmersingAutomation.Enums;

/// <summary>
/// 描述自动化错误发生的来源。
/// </summary>
public enum AutomationErrorSource
{
    /// <summary>触发器在轮询或验证时失败。</summary>
    Trigger,

    /// <summary>规则集验证失败。</summary>
    Ruleset,

    /// <summary>动作执行失败。</summary>
    Action,

    /// <summary>动作回滚失败。</summary>
    ActionReversion,

    /// <summary>工作流初始化、运行或销毁失败。</summary>
    Workflow,

    /// <summary>工作流持久化失败。</summary>
    Storage,

    /// <summary>状态变更订阅者失败。</summary>
    StatusChanged,

    /// <summary>资源释放失败。</summary>
    Disposal
}
