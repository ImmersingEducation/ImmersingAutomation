using ImmersingAutomation.Abstractions;
using ImmersingAutomation.Enums;
using ImmersingAutomation.Events;
using ImmersingAutomation.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
    public async Task IsEnabledChanges_WhenRaisedRapidly_ConvergeToLatestState()
    {
        var profile = new TestTriggerProfile();
        var workflow = TestWorkflows.Create(profile, isEnabled: true);
        var storage = new FakeAutomationStorageService();
        storage.Workflows.Add(workflow);

        await using var service = new AutomationService(storage)
        {
            TriggerPollInterval = TimeSpan.FromMinutes(1)
        };

        await service.InitializeAsync();

        // 事件处理器会被并发调度，不能假设它们按触发顺序执行。
        for (var i = 0; i < 25; i++)
        {
            workflow.IsEnabled = false;
            workflow.IsEnabled = true;
        }

        // 最终状态为启用：所有排队的变更处理完成后，应恰好存在一个运行器。
        await WaitUntilAsync(
            () => profile.InitializeCount - profile.ReleaseCount == 1,
            "A runner should exist after the workflow is enabled.");

        // 最终状态为禁用：运行器必须被释放。
        workflow.IsEnabled = false;
        await WaitUntilAsync(
            () => profile.ReleaseCount == profile.InitializeCount,
            "The runner should be released after the workflow is disabled.");

        // 再次启用时必须重新创建并初始化运行器。
        workflow.IsEnabled = true;
        await WaitUntilAsync(
            () => profile.InitializeCount - profile.ReleaseCount == 1,
            "A runner should be created after the workflow is re-enabled.");

        Assert.True(profile.InitializeCount >= 2);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string message)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);

        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException(message);
            }

            await Task.Delay(10);
        }
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
    public async Task ActionFailure_IsForwardedThroughServiceErrorOccurred()
    {
        var failingProfile = new TestActionProfile
        {
            RunException = new InvalidOperationException("run failed")
        };
        var workflow = TestWorkflows.Create(
            new TestTriggerProfile { ShouldTrigger = true },
            actionProfiles: [failingProfile],
            isReversionEnabled: true,
            isEnabled: true);
        var storage = new FakeAutomationStorageService();
        storage.Workflows.Add(workflow);

        await using var service = new AutomationService(storage)
        {
            TriggerPollInterval = TimeSpan.FromMilliseconds(5),
            ReversionPollInterval = TimeSpan.FromMinutes(1)
        };

        var errorCompletion = new TaskCompletionSource<AutomationErrorEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        service.ErrorOccurred += (_, eventArgs) => errorCompletion.TrySetResult(eventArgs);

        await service.InitializeAsync();

        var error = await errorCompletion.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(AutomationErrorSource.Action, error.Source);
        Assert.NotNull(error.Runner);
        Assert.IsType<TestAction>(error.Action);
    }

    [Fact]
    public async Task AddWorkflowRunner_RegistersTriggersForPolling()
    {
        var profile = new TestTriggerProfile();
        var triggered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        profile.Validator = () =>
        {
            triggered.TrySetResult();
            return true;
        };
        var workflow = TestWorkflows.Create(profile);

        await using var service = new AutomationService(new FakeAutomationStorageService())
        {
            TriggerPollInterval = TimeSpan.FromMilliseconds(5)
        };

        await service.AddWorkflowRunner(workflow);

        using var cancellationSource = new CancellationTokenSource();
        var loop = service.TriggerQueryLoop(cancellationSource.Token);

        await triggered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await cancellationSource.CancelAsync();
        await loop.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task InitializeAsync_WhenCalledConcurrently_InitializesOnce()
    {
        var storage = new FakeAutomationStorageService
        {
            LoadWorkflowsStarted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously),
            LoadWorkflowsGate = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously)
        };
        storage.Workflows.Add(TestWorkflows.Create(new TestTriggerProfile(), isEnabled: true));

        await using var service = new AutomationService(storage)
        {
            TriggerPollInterval = TimeSpan.FromMinutes(1)
        };

        var first = service.InitializeAsync();
        await storage.LoadWorkflowsStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = service.InitializeAsync();

        storage.LoadWorkflowsGate.TrySetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(service.IsInitialized);
        Assert.Equal(1, storage.LoadWorkflowsCallCount);
    }

    [Fact]
    public async Task InitializeAsync_WhenRunnerInitializationFails_RollsBackCreatedRunners()
    {
        var validProfile = new TestTriggerProfile();
        var storage = new FakeAutomationStorageService();
        storage.Workflows.Add(TestWorkflows.Create(validProfile, isEnabled: true));
        storage.Workflows.Add(TestWorkflows.Create(new UnattributedTriggerProfile(), isEnabled: true));

        await using var service = new AutomationService(storage)
        {
            TriggerPollInterval = TimeSpan.FromMinutes(1)
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.InitializeAsync());

        Assert.False(service.IsInitialized);
        Assert.Equal(1, validProfile.ReleaseCount);
    }

    [Fact]
    public async Task InitializeAsync_WhenWorkflowFailsAndFailFastIsDisabled_SkipsItAndContinues()
    {
        var validProfile = new TestTriggerProfile();
        var storage = new FakeAutomationStorageService();
        storage.Workflows.Add(TestWorkflows.Create(validProfile, isEnabled: true));
        storage.Workflows.Add(TestWorkflows.Create(new UnattributedTriggerProfile(), isEnabled: true));

        await using var service = new AutomationService(storage)
        {
            FailFastOnWorkflowInitializationError = false,
            TriggerPollInterval = TimeSpan.FromMinutes(1)
        };

        AutomationErrorEventArgs? error = null;
        service.ErrorOccurred += (_, eventArgs) => error ??= eventArgs;

        await service.InitializeAsync();

        Assert.True(service.IsInitialized);
        Assert.Equal(1, validProfile.InitializeCount);
        Assert.NotNull(error);
        Assert.Equal(AutomationErrorSource.Workflow, error.Source);
    }

    [Fact]
    public async Task InitializeAsync_WhenWorkflowFails_LogsError()
    {
        var loggerFactory = new RecordingLoggerFactory();
        var storage = new FakeAutomationStorageService();
        storage.Workflows.Add(TestWorkflows.Create(new UnattributedTriggerProfile(), isEnabled: true));

        await using var service = new AutomationService(storage, loggerFactory);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.InitializeAsync());

        Assert.Contains(
            loggerFactory.Entries,
            entry => entry.Level == LogLevel.Error
                     && entry.Exception is InvalidOperationException);
    }

    [Fact]
    public async Task DisposeAsync_DisposesOwnedServiceProvider()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddSingleton<DisposableSingleton>();

        var service = new AutomationService(new FakeAutomationStorageService(), services);
        await service.InitializeAsync();

        var singleton = (DisposableSingleton)service.ServiceProvider!
            .GetService(typeof(DisposableSingleton))!;

        await service.DisposeAsync();

        Assert.True(singleton.IsDisposed);
    }

    [Fact]
    public async Task MethodsAfterDispose_ThrowObjectDisposedException()
    {
        var service = new AutomationService(new FakeAutomationStorageService());
        await service.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => service.InitializeAsync());
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => service.AddWorkflowRunner(TestWorkflows.Create()));
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => service.RemoveWorkflowRunner(TestWorkflows.Create()));
    }

    [Fact]
    public async Task StopTriggerQueryLoopAsync_ThenStartingAgain_PollsTriggers()
    {
        var profile = new TestTriggerProfile();
        var workflow = TestWorkflows.Create(profile, isEnabled: true);
        var storage = new FakeAutomationStorageService();
        storage.Workflows.Add(workflow);

        await using var service = new AutomationService(storage)
        {
            TriggerPollInterval = TimeSpan.FromMilliseconds(5)
        };

        await service.InitializeAsync();
        await service.StopTriggerQueryLoopAsync();

        var validated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        profile.Validator = () =>
        {
            validated.TrySetResult();
            return false;
        };

        using var cancellationSource = new CancellationTokenSource();
        var loop = service.TriggerQueryLoop(cancellationSource.Token);

        await validated.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await cancellationSource.CancelAsync();
        await loop.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ServiceCollection_WhenServiceIsInitialized_Throws()
    {
        var service = new AutomationService(new FakeAutomationStorageService());
        await service.InitializeAsync();

        Assert.Throws<InvalidOperationException>(
            () => service.ServiceCollection = new ServiceCollection());

        await service.DisposeAsync();
    }

    [Fact]
    public async Task ServiceProvider_WhenSet_DoesNotMarkServiceAsInitialized()
    {
        var service = new AutomationService(new FakeAutomationStorageService());
        using var provider = new ServiceCollection().BuildServiceProvider();

        service.ServiceProvider = provider;

        Assert.False(service.IsInitialized);

        await service.InitializeAsync();

        Assert.True(service.IsInitialized);

        await service.DisposeAsync();
    }

    [Fact]
    public void PollIntervals_WhenInvalid_Throw()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new AutomationService(new FakeAutomationStorageService())
            {
                TriggerPollInterval = TimeSpan.Zero
            });

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new AutomationService(new FakeAutomationStorageService())
            {
                ReversionPollInterval = TimeSpan.FromMilliseconds(-1)
            });

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new AutomationService(new FakeAutomationStorageService())
            {
                ReversionCheckConcurrency = 0
            });
    }
}
