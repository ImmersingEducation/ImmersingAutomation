using ImmersingAutomation.Abstractions;
using ImmersingAutomation.Enums;
using ImmersingAutomation.Models;

namespace ImmersingAutomation.Events;

/// <summary>
/// 提供后台自动化操作失败时的上下文。
/// </summary>
/// <remarks>
/// 框架会在无法向调用方直接抛出异常的后台路径中引发此事件，
/// 例如触发器轮询、异步触发处理和存储文件加载。
/// 订阅者应自行记录或处理异常。
/// </remarks>
public class AutomationErrorEventArgs : EventArgs
{
    /// <summary>
    /// 获取错误来源。
    /// </summary>
    public AutomationErrorSource Source { get; }

    /// <summary>
    /// 获取发生错误的操作名称。
    /// </summary>
    public string Operation { get; }

    /// <summary>
    /// 获取导致错误的异常。
    /// </summary>
    public Exception Exception { get; }

    /// <summary>
    /// 获取与错误关联的工作流运行器（如果存在）。
    /// </summary>
    public WorkflowRunner? Runner { get; }

    /// <summary>
    /// 获取与错误关联的动作（如果存在）。
    /// </summary>
    public ActionBase? Action { get; }

    /// <summary>
    /// 获取与错误关联的触发器（如果存在）。
    /// </summary>
    public TriggerBase? Trigger { get; }

    /// <summary>
    /// 初始化 <see cref="AutomationErrorEventArgs"/> 类的新实例。
    /// </summary>
    /// <param name="source">错误来源。</param>
    /// <param name="operation">发生错误的操作名称。</param>
    /// <param name="exception">导致错误的异常。</param>
    /// <param name="runner">与错误关联的工作流运行器。</param>
    /// <param name="action">与错误关联的动作。</param>
    /// <param name="trigger">与错误关联的触发器。</param>
    public AutomationErrorEventArgs(
        AutomationErrorSource source,
        string operation,
        Exception exception,
        WorkflowRunner? runner = null,
        ActionBase? action = null,
        TriggerBase? trigger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(exception);

        Source = source;
        Operation = operation;
        Exception = exception;
        Runner = runner;
        Action = action;
        Trigger = trigger;
    }
}
