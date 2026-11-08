using ImmersingAutomation.Enums;
using ImmersingAutomation.Events;
using ImmersingAutomation.Models;
using Microsoft.Extensions.DependencyInjection;

namespace ImmersingAutomation.Tests;

public class WorkflowRunnerTests
{
    [Fact]
    public async Task InitializeAsync_CreatesTriggersActionsAndRuleset()
    {
        var triggerProfile = new TestTriggerProfile();
        var actionProfile = new TestActionProfile();
        var ruleProfile = new TestRuleProfile { Result = true };
        var rulesetProfile = TestWorkflows.CreateRuleset(
            RulesetMode.All,
            new TestRule { Profile = ruleProfile });
        var workflow = TestWorkflows.Create(
            triggerProfile,
            rulesetProfile,
            [actionProfile]);
        await using var runner = new WorkflowRunner(workflow);
        var statuses = new List<WorkflowRunnerStatus>();
        runner.StatusChanged += (_, e) => statuses.Add(e.Status);

        await runner.InitializeAsync();

        Assert.True(runner.Initialized);
        Assert.Equal(WorkflowRunnerStatus.WaitingForTriggering, runner.Status);
        Assert.Equal(
            [
                WorkflowRunnerStatus.Initializing,
                WorkflowRunnerStatus.ExternalInitializing,
                WorkflowRunnerStatus.WaitingForTriggering
            ],
            statuses);

        var trigger = Assert.IsType<TestTrigger>(Assert.Single(runner.Triggers));
        Assert.Same(triggerProfile, trigger.Profile);
        Assert.Equal(TriggerStatus.Ready, trigger.Status);
        Assert.Equal(1, triggerProfile.InitializeCount);

        var action = Assert.IsType<TestAction>(Assert.Single(runner.Actions));
        Assert.Same(actionProfile, action.Profile);

        Assert.Same(rulesetProfile, runner.Ruleset.Profile);
    }

    [Fact]
    public async Task InitializeAsync_WhenAlreadyInitialized_DoesNotCreateDuplicates()
    {
        var triggerProfile = new TestTriggerProfile();
        var workflow = TestWorkflows.Create(triggerProfile);
        await using var runner = new WorkflowRunner(workflow);

        await runner.InitializeAsync();
        await runner.InitializeAsync();

        Assert.Single(runner.Triggers);
        Assert.Equal(1, triggerProfile.InitializeCount);
    }

    [Fact]
    public async Task InitializeAsync_WhenTriggerProfileHasNoAttribute_Throws()
    {
        var workflow = TestWorkflows.Create(new UnattributedTriggerProfile());
        await using var runner = new WorkflowRunner(workflow);

        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.InitializeAsync());

        Assert.False(runner.Initialized);
        Assert.Empty(runner.Triggers);
        Assert.Empty(runner.Actions);
    }

    [Fact]
    public async Task InitializeAsync_WhenActionProfileHasNoAttribute_Throws()
    {
        var workflow = TestWorkflows.Create(
            new TestTriggerProfile(),
            actionProfiles: [new UnattributedActionProfile()]);
        await using var runner = new WorkflowRunner(workflow);

        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.InitializeAsync());

        Assert.False(runner.Initialized);
        Assert.Empty(runner.Triggers);
        Assert.Empty(runner.Actions);
    }

    [Fact]
    public async Task InitializeAsync_WhenTriggerProfileTypeMismatches_Throws()
    {
        var workflow = TestWorkflows.Create(new MismatchedTriggerProfile());
        await using var runner = new WorkflowRunner(workflow);

        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.InitializeAsync());

        Assert.False(runner.Initialized);
        Assert.Empty(runner.Triggers);
    }

    [Fact]
    public async Task InitializeAsync_WhenActionProfileTypeMismatches_Throws()
    {
        var workflow = TestWorkflows.Create(
            new TestTriggerProfile(),
            actionProfiles: [new MismatchedActionProfile()]);
        await using var runner = new WorkflowRunner(workflow);

        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.InitializeAsync());

        Assert.False(runner.Initialized);
        Assert.Empty(runner.Actions);
    }

    [Fact]
    public async Task StatusChanged_WhenSubscriberThrows_DoesNotBreakInitialization()
    {
        var workflow = TestWorkflows.Create(new TestTriggerProfile());
        await using var runner = new WorkflowRunner(workflow);
        runner.StatusChanged += (_, _) =>
            throw new InvalidOperationException("faulty subscriber");

        await runner.InitializeAsync();

        Assert.True(runner.Initialized);
        Assert.Equal(WorkflowRunnerStatus.WaitingForTriggering, runner.Status);
    }

    [Fact]
    public async Task Trigger_WhenRulesetMatches_RunsActionsAndWaitsForReversion()
    {
        var ruleProfile = new TestRuleProfile { Result = true };
        var actionProfile = new TestActionProfile();
        var workflow = TestWorkflows.Create(
            new TestTriggerProfile { ShouldTrigger = true },
            TestWorkflows.CreateRuleset(
                RulesetMode.All,
                new TestRule { Profile = ruleProfile }),
            [actionProfile],
            isReversionEnabled: true);
        await using var runner = new WorkflowRunner(workflow);
        await runner.InitializeAsync();

        var waitingForReversion = WaitForStatusAsync(
            runner,
            WorkflowRunnerStatus.WaitingForReversion);
        Assert.True(runner.Triggers[0].Trigger());
        await waitingForReversion;

        Assert.Equal(1, ruleProfile.ValidateCount);
        Assert.Equal(1, actionProfile.RunCount);
        Assert.Equal(WorkflowRunnerStatus.WaitingForReversion, runner.Status);

        var waitingForTriggering = WaitForStatusAsync(
            runner,
            WorkflowRunnerStatus.WaitingForTriggering);
        await runner.OnReversionTriggered(runner, EventArgs.Empty);
        await waitingForTriggering;

        Assert.Equal(1, actionProfile.RevertCount);
        Assert.Equal(ActionStatus.WaitingForRunning, runner.Actions[0].Status);
    }

    [Fact]
    public async Task Trigger_WhenRulesetDoesNotMatch_DoesNotRunActions()
    {
        var ruleProfile = new TestRuleProfile { Result = false };
        var actionProfile = new TestActionProfile();
        var workflow = TestWorkflows.Create(
            new TestTriggerProfile { ShouldTrigger = true },
            TestWorkflows.CreateRuleset(
                RulesetMode.All,
                new TestRule { Profile = ruleProfile }),
            [actionProfile],
            isReversionEnabled: true);
        await using var runner = new WorkflowRunner(workflow);
        await runner.InitializeAsync();

        var waitingForTriggering = WaitForStatusAsync(
            runner,
            WorkflowRunnerStatus.WaitingForTriggering);
        Assert.True(runner.Triggers[0].Trigger());
        await waitingForTriggering;

        Assert.Equal(1, ruleProfile.ValidateCount);
        Assert.Equal(0, actionProfile.RunCount);
        Assert.Equal(WorkflowRunnerStatus.WaitingForTriggering, runner.Status);
    }

    [Fact]
    public async Task ExceptionSkipMode_ContinuesAndDoesNotRevertFailedAction()
    {
        var failingProfile = new TestActionProfile
        {
            RunException = new InvalidOperationException("run failed")
        };
        var succeedingProfile = new TestActionProfile();
        var workflow = TestWorkflows.Create(
            new TestTriggerProfile { ShouldTrigger = true },
            actionProfiles: [failingProfile, succeedingProfile],
            isReversionEnabled: true,
            exceptionHandleMode: WorkflowRunnerExceptionHandleMode.Skip);
        await using var runner = new WorkflowRunner(workflow);
        await runner.InitializeAsync();

        var waitingForReversion = WaitForStatusAsync(
            runner,
            WorkflowRunnerStatus.WaitingForReversion);
        runner.Triggers[0].Trigger();
        await waitingForReversion;

        Assert.Equal(1, failingProfile.RunCount);
        Assert.Equal(1, succeedingProfile.RunCount);

        var waitingForTriggering = WaitForStatusAsync(
            runner,
            WorkflowRunnerStatus.WaitingForTriggering);
        await runner.OnReversionTriggered(runner, EventArgs.Empty);
        await waitingForTriggering;

        Assert.Equal(0, failingProfile.RevertCount);
        Assert.Equal(1, succeedingProfile.RevertCount);
    }

    [Fact]
    public async Task ExceptionInterruptMode_StopsRemainingActionsAndRevertsFailedAction()
    {
        var failingProfile = new TestActionProfile
        {
            RunException = new InvalidOperationException("run failed")
        };
        var succeedingProfile = new TestActionProfile();
        var workflow = TestWorkflows.Create(
            new TestTriggerProfile { ShouldTrigger = true },
            actionProfiles: [failingProfile, succeedingProfile],
            isReversionEnabled: true,
            exceptionHandleMode: WorkflowRunnerExceptionHandleMode.Interrupt);
        await using var runner = new WorkflowRunner(workflow);
        await runner.InitializeAsync();

        var waitingForTriggering = WaitForStatusAsync(
            runner,
            WorkflowRunnerStatus.WaitingForTriggering);
        runner.Triggers[0].Trigger();
        await waitingForTriggering;

        Assert.Equal(1, failingProfile.RunCount);
        Assert.Equal(0, succeedingProfile.RunCount);
        Assert.Equal(1, failingProfile.RevertCount);
        Assert.Equal(0, succeedingProfile.RevertCount);
        Assert.Equal(WorkflowRunnerStatus.WaitingForTriggering, runner.Status);
    }

    [Fact]
    public async Task Interrupt_WhileRunning_RevertsRunningAction()
    {
        var actionProfile = new TestActionProfile
        {
            RunStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            RunGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var workflow = TestWorkflows.Create(
            new TestTriggerProfile { ShouldTrigger = true },
            actionProfiles: [actionProfile],
            isReversionEnabled: true);
        await using var runner = new WorkflowRunner(workflow);
        await runner.InitializeAsync();

        var waitingForTriggering = WaitForStatusAsync(
            runner,
            WorkflowRunnerStatus.WaitingForTriggering);
        runner.Triggers[0].Trigger();
        await actionProfile.RunStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(WorkflowRunnerStatus.Running, runner.Status);
        Assert.True(runner.Interrupt());
        await waitingForTriggering;

        Assert.Equal(1, actionProfile.RunCount);
        Assert.Equal(1, actionProfile.RevertCount);
        Assert.Equal(1, actionProfile.InterruptionObservedCount);
        Assert.Equal(WorkflowRunnerStatus.WaitingForTriggering, runner.Status);
    }

    [Fact]
    public async Task Interrupt_WhileWaitingForReversion_ResetsWithoutReverting()
    {
        var actionProfile = new TestActionProfile();
        var workflow = TestWorkflows.Create(
            new TestTriggerProfile { ShouldTrigger = true },
            actionProfiles: [actionProfile],
            isReversionEnabled: true);
        await using var runner = new WorkflowRunner(workflow);
        await runner.InitializeAsync();

        var waitingForReversion = WaitForStatusAsync(
            runner,
            WorkflowRunnerStatus.WaitingForReversion);
        runner.Triggers[0].Trigger();
        await waitingForReversion;

        Assert.True(runner.Interrupt());
        Assert.Equal(WorkflowRunnerStatus.WaitingForTriggering, runner.Status);
        Assert.Equal(ActionStatus.WaitingForRunning, runner.Actions[0].Status);

        await runner.OnReversionTriggered(runner, EventArgs.Empty);

        Assert.Equal(0, actionProfile.RevertCount);
    }

    [Fact]
    public async Task OnReversionTriggered_WhenNothingWasExecuted_ReturnsToTriggering()
    {
        var workflow = TestWorkflows.Create(
            new TestTriggerProfile { ShouldTrigger = true },
            isReversionEnabled: true);
        await using var runner = new WorkflowRunner(workflow);
        await runner.InitializeAsync();

        var waitingForReversion = WaitForStatusAsync(
            runner,
            WorkflowRunnerStatus.WaitingForReversion);
        runner.Triggers[0].Trigger();
        await waitingForReversion;

        Assert.Empty(runner.Actions);

        var waitingForTriggering = WaitForStatusAsync(
            runner,
            WorkflowRunnerStatus.WaitingForTriggering);
        await runner.OnReversionTriggered(runner, EventArgs.Empty);
        await waitingForTriggering;

        Assert.Equal(WorkflowRunnerStatus.WaitingForTriggering, runner.Status);
    }

    [Fact]
    public async Task DisposeAsync_DisposesRunnerAndTriggers()
    {
        var workflow = TestWorkflows.Create(new TestTriggerProfile());
        var runner = new WorkflowRunner(workflow);
        await runner.InitializeAsync();
        var trigger = Assert.Single(runner.Triggers);

        await runner.DisposeAsync();

        Assert.Equal(WorkflowRunnerStatus.Destroying, runner.Status);
        Assert.False(runner.Initialized);
        Assert.Empty(runner.Triggers);
        Assert.Empty(runner.Actions);
        Assert.Equal(TriggerStatus.Disposed, trigger.Status);
    }

    [Fact]
    public async Task DisposeAsync_WhenCalledTwice_IsIdempotent()
    {
        var workflow = TestWorkflows.Create(new TestTriggerProfile());
        var runner = new WorkflowRunner(workflow);
        await runner.InitializeAsync();

        await runner.DisposeAsync();
        await runner.DisposeAsync();

        Assert.Equal(WorkflowRunnerStatus.Destroying, runner.Status);
    }

    [Fact]
    public async Task InitializeAsync_UsesServiceProviderForTriggerDependencies()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddSingleton<TestDependency>();

        await using var provider = services.BuildServiceProvider();
        var workflow = TestWorkflows.Create(new DependencyTriggerProfile());
        await using var runner = new WorkflowRunner(workflow, provider);

        await runner.InitializeAsync();

        var trigger = Assert.IsType<DependencyTrigger>(Assert.Single(runner.Triggers));
        Assert.Equal(42, trigger.Dependency.Value);
    }

    [Fact]
    public async Task InitializeAsync_WhenReversionIsEnabledForUnsupportedAction_Throws()
    {
        var workflow = TestWorkflows.Create(
            new TestTriggerProfile(),
            actionProfiles: [new NonReversibleActionProfile()],
            isReversionEnabled: true);
        await using var runner = new WorkflowRunner(workflow);

        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.InitializeAsync());

        Assert.False(runner.Initialized);
        Assert.Empty(runner.Actions);
    }

    [Fact]
    public async Task InitializeAsync_WhenReversionIsDisabledForUnsupportedAction_Succeeds()
    {
        var workflow = TestWorkflows.Create(
            new TestTriggerProfile(),
            actionProfiles: [new NonReversibleActionProfile()],
            isReversionEnabled: false);
        await using var runner = new WorkflowRunner(workflow);

        await runner.InitializeAsync();

        Assert.True(runner.Initialized);
        Assert.Single(runner.Actions);
    }

    [Fact]
    public async Task ActionFailure_RaisesErrorOccurred()
    {
        var failingProfile = new TestActionProfile
        {
            RunException = new InvalidOperationException("run failed")
        };
        var workflow = TestWorkflows.Create(
            new TestTriggerProfile { ShouldTrigger = true },
            actionProfiles: [failingProfile],
            isReversionEnabled: true,
            exceptionHandleMode: WorkflowRunnerExceptionHandleMode.Skip);
        await using var runner = new WorkflowRunner(workflow);
        await runner.InitializeAsync();

        var errorCompletion = new TaskCompletionSource<AutomationErrorEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        runner.ErrorOccurred += (_, eventArgs) => errorCompletion.TrySetResult(eventArgs);

        var waitingForReversion = WaitForStatusAsync(
            runner,
            WorkflowRunnerStatus.WaitingForReversion);
        runner.Triggers[0].Trigger();

        var error = await errorCompletion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await waitingForReversion;

        Assert.Equal(AutomationErrorSource.Action, error.Source);
        Assert.IsType<TestAction>(error.Action);
        Assert.Same(runner, error.Runner);
    }

    private static Task WaitForStatusAsync(
        WorkflowRunner runner,
        WorkflowRunnerStatus expectedStatus)
    {
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        EventHandler<WorkflowRunnerStatusChangedEventArgs> handler = null!;
        handler = (_, eventArgs) =>
        {
            if (eventArgs.Status != expectedStatus)
            {
                return;
            }

            runner.StatusChanged -= handler;
            completion.TrySetResult();
        };

        runner.StatusChanged += handler;

        return completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
