namespace ImmersingAutomation.Tests;

public class WorkflowTests
{
    [Fact]
    public void NewWorkflow_HasGeneratedGuidAndDefaults()
    {
        var workflow = new Models.Workflow();

        Assert.NotEqual(Guid.Empty, workflow.Guid);
        Assert.False(workflow.IsEnabled);
        Assert.False(workflow.IsReversionEnabled);
        Assert.Equal(string.Empty, workflow.Name);
        Assert.Empty(workflow.TriggerProfiles);
        Assert.Empty(workflow.ActionProfiles);
        Assert.NotNull(workflow.RulesetProfile);
        Assert.Empty(workflow.RulesetProfile.Rules);
    }

    [Fact]
    public void IsEnabledChanged_IsRaisedWheneverValueIsSet()
    {
        var workflow = new Models.Workflow();
        var raised = 0;
        object? sender = null;
        workflow.IsEnabledChanged += (s, _) =>
        {
            sender = s;
            raised++;
        };

        workflow.IsEnabled = true;
        workflow.IsEnabled = true;

        Assert.Equal(2, raised);
        Assert.Same(workflow, sender);
    }

    [Fact]
    public void TwoWorkflows_GetDifferentGuids()
    {
        var first = new Models.Workflow();
        var second = new Models.Workflow();

        Assert.NotEqual(first.Guid, second.Guid);
    }
}
