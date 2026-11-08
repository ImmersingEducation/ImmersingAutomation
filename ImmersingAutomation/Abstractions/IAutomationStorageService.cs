using ImmersingAutomation.Models;

namespace ImmersingAutomation.Abstractions;

public interface IAutomationStorageService
{
    string StoragePath { get; }

    string SerializeWorkflow(Workflow workflow);

    Workflow DeserializeWorkflow(string json);

    Task SaveWorkflowAsync(Workflow workflow, CancellationToken cancellationToken = default);

    Task<Workflow?> LoadWorkflowAsync(Guid workflowId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Workflow>> LoadWorkflowsAsync(CancellationToken cancellationToken = default);

    bool WorkflowExists(Guid workflowId);

    Task<bool> DeleteWorkflowAsync(Guid workflowId, CancellationToken cancellationToken = default);
}
