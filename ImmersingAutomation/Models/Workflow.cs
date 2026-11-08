using ImmersingAutomation.Abstractions;
using ImmersingAutomation.Enums;

namespace ImmersingAutomation.Models;

/// <summary>
/// 描述一个自动化工作流：由什么触发、需要满足哪些规则以及执行什么操作。
/// </summary>
public class Workflow
{
    /// <summary>
    /// 当前支持的工作流持久化架构版本。
    /// </summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    /// 获取工作流的唯一标识符。
    /// </summary>
    public Guid Guid { get; init; } = Guid.NewGuid();

    /// <summary>
    /// 获取工作流的持久化架构版本。
    /// </summary>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    
    /// <summary>
    /// 获取或设置工作流的显示名称。
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 获取或设置工作流是否启用。
    /// </summary>
    /// <remarks>
    /// 更改此属性会引发 <see cref="IsEnabledChanged"/>。
    /// </remarks>
    public bool IsEnabled
    {
        get;
        set
        {
            field = value;
            IsEnabledChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// 获取或设置启动工作流的触发器配置。
    /// </summary>
    public List<TriggerProfileBase> TriggerProfiles { get; set; } = [];
    
    /// <summary>
    /// 获取或设置执行动作前必须匹配的规则集。
    /// </summary>
    public RulesetProfile RulesetProfile { get; set; } = new();
    
    /// <summary>
    /// 获取或设置工作流执行的动作配置。
    /// </summary>
    public List<ActionProfileBase> ActionProfiles { get; set; } = [];
    
    /// <summary>
    /// 获取或设置动作执行后是否需要回滚。
    /// </summary>
    public bool IsReversionEnabled { get; set; }
    
    /// <summary>
    /// 获取或设置工作流运行器处理失败动作的方式。
    /// </summary>
    public WorkflowRunnerExceptionHandleMode ExceptionHandleMode { get; set; }

    /// <summary>
    /// 当 <see cref="IsEnabled"/> 变化时发生。
    /// </summary>
    public event EventHandler? IsEnabledChanged;
}
