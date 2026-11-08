using ImmersingAutomation.Models;

namespace ImmersingAutomation.Abstractions;

/// <summary>
/// 负责持久化和还原 <see cref="Workflow"/> 实例。
/// </summary>
public interface IAutomationStorageService
{
    /// <summary>
    /// 获取存储工作流的目录。
    /// </summary>
    string StoragePath { get; }

    /// <summary>
    /// 将工作流序列化为 JSON。
    /// </summary>
    /// <param name="workflow">要序列化的工作流。</param>
    /// <returns><paramref name="workflow"/> 的 JSON 表示。</returns>
    string SerializeWorkflow(Workflow workflow);

    /// <summary>
    /// 从 JSON 反序列化工作流。
    /// </summary>
    /// <param name="json">工作流的 JSON 表示。</param>
    /// <returns>反序列化后的工作流。</returns>
    Workflow DeserializeWorkflow(string json);

    /// <summary>
    /// 保存工作流，并覆盖标识相同的已存储工作流。
    /// </summary>
    /// <param name="workflow">要保存的工作流。</param>
    /// <param name="cancellationToken">用于取消操作的标记。</param>
    Task SaveWorkflowAsync(Workflow workflow, CancellationToken cancellationToken = default);

    /// <summary>
    /// 根据标识符加载工作流。
    /// </summary>
    /// <param name="workflowId">工作流的标识符。</param>
    /// <param name="cancellationToken">用于取消操作的标记。</param>
    /// <returns>工作流；不存在时返回 <see langword="null"/>。</returns>
    Task<Workflow?> LoadWorkflowAsync(Guid workflowId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 加载所有已存储的工作流。
    /// </summary>
    /// <param name="cancellationToken">用于取消操作的标记。</param>
    /// <returns>已存储的工作流。</returns>
    Task<IReadOnlyList<Workflow>> LoadWorkflowsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 确定是否存在指定标识符的工作流。
    /// </summary>
    /// <param name="workflowId">工作流的标识符。</param>
    bool WorkflowExists(Guid workflowId);

    /// <summary>
    /// 删除已存储的工作流。
    /// </summary>
    /// <param name="workflowId">工作流的标识符。</param>
    /// <param name="cancellationToken">用于取消操作的标记。</param>
    /// <returns>删除了工作流时返回 <see langword="true"/>。</returns>
    Task<bool> DeleteWorkflowAsync(Guid workflowId, CancellationToken cancellationToken = default);
}
