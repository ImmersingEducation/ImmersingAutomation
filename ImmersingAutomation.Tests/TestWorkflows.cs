using ImmersingAutomation.Abstractions;
using ImmersingAutomation.Enums;
using ImmersingAutomation.Models;

namespace ImmersingAutomation.Tests;

internal static class TestWorkflows
{
    public static Workflow Create(
        TriggerProfileBase? triggerProfile = null,
        RulesetProfile? rulesetProfile = null,
        IEnumerable<ActionProfileBase>? actionProfiles = null,
        bool isReversionEnabled = false,
        WorkflowRunnerExceptionHandleMode exceptionHandleMode =
            WorkflowRunnerExceptionHandleMode.Skip,
        bool isEnabled = false)
    {
        return new Workflow
        {
            Name = "Test workflow",
            IsEnabled = isEnabled,
            TriggerProfiles = triggerProfile is null ? [] : [triggerProfile],
            RulesetProfile = rulesetProfile ?? new RulesetProfile
            {
                Mode = RulesetMode.All,
                Rules = []
            },
            ActionProfiles = actionProfiles?.ToList() ?? [],
            IsReversionEnabled = isReversionEnabled,
            ExceptionHandleMode = exceptionHandleMode
        };
    }

    public static RulesetProfile CreateRuleset(RulesetMode mode, params RuleBase[] rules)
    {
        return new RulesetProfile
        {
            Mode = mode,
            Rules = rules.ToList()
        };
    }

    public static TestRule CreateRule(bool result, bool isReversed = false)
    {
        return new TestRule
        {
            Profile = new TestRuleProfile { Result = result },
            IsReversed = isReversed
        };
    }
}

internal sealed class FakeAutomationStorageService : IAutomationStorageService
{
    public List<Workflow> Workflows { get; } = [];

    public int LoadWorkflowsCallCount { get; private set; }

    public Exception? LoadWorkflowsException { get; set; }

    public string StoragePath => "/fake-storage";

    public string SerializeWorkflow(Workflow workflow)
    {
        throw new NotSupportedException();
    }

    public Workflow DeserializeWorkflow(string json)
    {
        throw new NotSupportedException();
    }

    public Task SaveWorkflowAsync(Workflow workflow, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public Task<Workflow?> LoadWorkflowAsync(Guid workflowId, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public Task<IReadOnlyList<Workflow>> LoadWorkflowsAsync(CancellationToken cancellationToken = default)
    {
        LoadWorkflowsCallCount++;

        if (LoadWorkflowsException is not null)
        {
            var exception = LoadWorkflowsException;
            LoadWorkflowsException = null;
            throw exception;
        }

        return Task.FromResult<IReadOnlyList<Workflow>>(Workflows.ToList());
    }

    public bool WorkflowExists(Guid workflowId)
    {
        throw new NotSupportedException();
    }

    public Task<bool> DeleteWorkflowAsync(Guid workflowId, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }
}
