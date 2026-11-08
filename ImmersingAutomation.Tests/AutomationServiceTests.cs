using ImmersingAutomation.Abstractions;
using ImmersingAutomation.Enums;
using ImmersingAutomation.Models;

namespace ImmersingAutomation.Tests;

public class AutomationServiceTests
{
    [Fact]
    public async Task InitializeAsync_LoadsWorkflowsAndStartsTriggerPolling()
    {
        var profile = new TestTriggerProfile();
        var triggered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        profile.Validator = () =>
        {
            triggered.TrySetResult();
            return true;
        };

        var workflow = TestWorkflows.Create(profile, isEnabled: true);
        var storage = new FakeAutomationStorageService();
        storage.Workflows.Add(workflow);

        await using var service = new AutomationService(storage)
        {
            TriggerPollInterval = TimeSpan.FromMilliseconds(5)
        };

        await service.InitializeAsync();

        Assert.True(service.IsInitialized);
        Assert.Null(service.ServiceCollection);
        Assert.Equal(1, storage.LoadWorkflowsCallCount);

        await triggered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(profile.ValidateCount > 0);
    }

    [Fact]
    public async Task InitializeAsync_WhenCalledTwice_LoadsWorkflowsOnce()
    {
        var storage = new FakeAutomationStorageService();
        storage.Workflows.Add(TestWorkflows.Create(new TestTriggerProfile(), isEnabled: true));

        await using var service = new AutomationService(storage)
        {
            TriggerPollInterval = TimeSpan.FromMinutes(1)
        };

        await service.InitializeAsync();
        await service.InitializeAsync();

        Assert.Equal(1, storage.LoadWorkflowsCallCount);
    }

    [Fact]
    public async Task InitializeAsync_WhenLoadingFails_CanBeRetried()
    {
        var storage = new FakeAutomationStorageService
        {
            LoadWorkflowsException = new InvalidOperationException("load failed")
        };
        storage.Workflows.Add(TestWorkflows.Create(new TestTriggerProfile(), isEnabled: true));

        await using var service = new AutomationService(storage)
        {
            TriggerPollInterval = TimeSpan.FromMinutes(1)
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.InitializeAsync());
        Assert.False(service.IsInitialized);

        await service.InitializeAsync();

        Assert.True(service.IsInitialized);
        Assert.Equal(2, storage.LoadWorkflowsCallCount);
    }

    [Fact]
    public async Task InitializeAsync_OnlyAddsEnabledWorkflows()
    {
        var enabledProfile = new TestTriggerProfile();
        var disabledProfile = new TestTriggerProfile();
        var enabledWorkflow = TestWorkflows.Create(enabledProfile, isEnabled: true);
        var disabledWorkflow = TestWorkflows.Create(disabledProfile, isEnabled: false);
        var storage = new FakeAutomationStorageService();
        storage.Workflows.Add(enabledWorkflow);
        storage.Workflows.Add(disabledWorkflow);

        await using var service = new AutomationService(storage)
        {
            TriggerPollInterval = TimeSpan.FromMinutes(1)
        };

        await service.InitializeAsync();

        Assert.Equal(1, enabledProfile.InitializeCount);
        Assert.Equal(0, disabledProfile.InitializeCount);

        disabledProfile.Initialized =
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        disabledWorkflow.IsEnabled = true;

        await disabledProfile.Initialized.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, disabledProfile.InitializeCount);

        enabledProfile.Released =
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        enabledWorkflow.IsEnabled = false;

        await enabledProfile.Released.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, enabledProfile.ReleaseCount);
    }

    [Fact]
    public async Task AddWorkflowRunner_WhenAlreadyAdded_DoesNotInitializeTwice()
    {
        var profile = new TestTriggerProfile();
        var workflow = TestWorkflows.Create(profile);
        await using var service = new AutomationService(new FakeAutomationStorageService());

        await service.AddWorkflowRunner(workflow);
        await service.AddWorkflowRunner(workflow);

        Assert.Equal(1, profile.InitializeCount);

        await service.RemoveWorkflowRunner(workflow);

        Assert.Equal(1, profile.ReleaseCount);
    }

    [Fact]
    public async Task RemoveWorkflowRunner_WhenNotPresent_DoesNothing()
    {
        await using var service = new AutomationService(new FakeAutomationStorageService());

        await service.RemoveWorkflowRunner(TestWorkflows.Create());
    }

    [Fact]
    public async Task AddWorkflowRunner_WhenInitializationFails_DoesNotKeepRunner()
    {
        var workflow = TestWorkflows.Create(new UnattributedTriggerProfile());
        await using var service = new AutomationService(new FakeAutomationStorageService());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.AddWorkflowRunner(workflow));

        await service.RemoveWorkflowRunner(workflow);
    }

    [Fact]
    public async Task DisposeAsync_StopsLoopsAndDisposesRunners()
    {
        var profile = new TestTriggerProfile();
        var storage = new FakeAutomationStorageService();
        storage.Workflows.Add(TestWorkflows.Create(profile, isEnabled: true));

        var service = new AutomationService(storage)
        {
            TriggerPollInterval = TimeSpan.FromMilliseconds(5),
            ReversionPollInterval = TimeSpan.FromMilliseconds(5)
        };

        await service.InitializeAsync();
        await service.DisposeAsync();
        await service.DisposeAsync();

        Assert.Equal(1, profile.ReleaseCount);
    }

    [Fact]
    public async Task TriggerQueryLoop_WhenAlreadyRunning_Throws()
    {
        await using var service = new AutomationService(new FakeAutomationStorageService());
        using var cancellationSource = new CancellationTokenSource();

        var loop = service.TriggerQueryLoop(cancellationSource.Token);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.TriggerQueryLoop());

        await cancellationSource.CancelAsync();
        await loop.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ReversionQueryLoop_WhenAlreadyRunning_Throws()
    {
        await using var service = new AutomationService(new FakeAutomationStorageService());
        using var cancellationSource = new CancellationTokenSource();

        var loop = service.ReversionQueryLoop(cancellationSource.Token);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ReversionQueryLoop());

        await cancellationSource.CancelAsync();
        await loop.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ReversionQueryLoop_RevertsWorkflowWhenRulesetStopsMatching()
    {
        var ruleProfile = new TestRuleProfile { Result = true };
        var actionProfile = new TestActionProfile();
        var reverted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        actionProfile.RevertCompleted = reverted;
        actionProfile.RunCompleted = () => ruleProfile.Result = false;

        var workflow = TestWorkflows.Create(
            new TestTriggerProfile { ShouldTrigger = true },
            TestWorkflows.CreateRuleset(
                RulesetMode.All,
                new TestRule { Profile = ruleProfile }),
            [actionProfile],
            isReversionEnabled: true,
            isEnabled: true);
        var storage = new FakeAutomationStorageService();
        storage.Workflows.Add(workflow);

        await using var service = new AutomationService(storage)
        {
            TriggerPollInterval = TimeSpan.FromMilliseconds(5),
            ReversionPollInterval = TimeSpan.FromMilliseconds(5),
            ReversionCheckConcurrency = 2
        };

        await service.InitializeAsync();

        await reverted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, actionProfile.RunCount);
        Assert.Equal(1, actionProfile.RevertCount);
    }

    [Fact]
    public async Task ExternallyInitializeWorkflowRunnerAsync_RegistersTriggers()
    {
        var profile = new TestTriggerProfile();
        var triggered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        profile.Validator = () =>
        {
            triggered.TrySetResult();
            return true;
        };
        var workflow = TestWorkflows.Create(profile);
        var runner = new WorkflowRunner(workflow);
        await runner.InitializeAsync();

        await using var service = new AutomationService(new FakeAutomationStorageService())
        {
            TriggerPollInterval = TimeSpan.FromMilliseconds(5)
        };

        using var cancellationSource = new CancellationTokenSource();
        await service.ExternallyInitializeWorkflowRunnerAsync(runner);

        var loop = service.TriggerQueryLoop(cancellationSource.Token);

        await triggered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await cancellationSource.CancelAsync();
        await loop.WaitAsync(TimeSpan.FromSeconds(5));

        await runner.DisposeAsync();
    }
}
