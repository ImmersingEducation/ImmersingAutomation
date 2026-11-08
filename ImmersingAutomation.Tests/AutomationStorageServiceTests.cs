using ImmersingAutomation.Enums;
using ImmersingAutomation.Models;
using Microsoft.Extensions.DependencyInjection;

namespace ImmersingAutomation.Tests;

public sealed class AutomationStorageServiceTests : IAsyncLifetime
{
    private readonly string _storagePath = Path.Combine(
        Path.GetTempPath(),
        "ImmersingAutomation.Tests",
        Guid.NewGuid().ToString("N"));

    private AutomationService _automationService = null!;
    private AutomationStorageService _storage = null!;

    public Task InitializeAsync()
    {
        _automationService = new AutomationService(new FakeAutomationStorageService());
        _automationService.ServiceCollection!.AddSingleton<TestTrigger>();
        _automationService.ServiceCollection!.AddSingleton<TestRule>();
        _automationService.ServiceCollection!.AddSingleton<TestAction>();
        _storage = new AutomationStorageService(_automationService, _storagePath);

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _automationService.DisposeAsync();

        if (Directory.Exists(_storagePath))
        {
            Directory.Delete(_storagePath, recursive: true);
        }
    }

    [Fact]
    public void StoragePath_IsMadeAbsolute()
    {
        var relativePath = Path.Combine(".", "storage");

        var storage = new AutomationStorageService(_automationService, relativePath);

        Assert.Equal(Path.GetFullPath(relativePath), storage.StoragePath);
    }

    [Fact]
    public void Constructor_RejectsInvalidArguments()
    {
        Assert.Throws<ArgumentNullException>(
            () => new AutomationStorageService(null!, _storagePath));
        Assert.Throws<ArgumentException>(
            () => new AutomationStorageService(_automationService, " "));
    }

    [Fact]
    public void SerializeAndDeserializeWorkflow_RoundTripsAllParts()
    {
        var triggerProfile = new TestTriggerProfile { ShouldTrigger = true };
        var ruleProfile = new TestRuleProfile { Result = true, ValidateCount = 7 };
        var actionProfile = new TestActionProfile { RunDelayMilliseconds = 42 };
        var rulesetProfile = TestWorkflows.CreateRuleset(
            RulesetMode.Any,
            new TestRule { Profile = ruleProfile, IsReversed = true });
        var workflow = TestWorkflows.Create(
            triggerProfile,
            rulesetProfile,
            [actionProfile],
            isReversionEnabled: true,
            exceptionHandleMode: WorkflowRunnerExceptionHandleMode.Interrupt);
        workflow.Name = "Round trip";

        var json = _storage.SerializeWorkflow(workflow);
        var result = _storage.DeserializeWorkflow(json);

        Assert.Contains("\"$type\"", json);
        Assert.Equal(workflow.Guid, result.Guid);
        Assert.Equal("Round trip", result.Name);
        Assert.True(result.IsReversionEnabled);
        Assert.Equal(WorkflowRunnerExceptionHandleMode.Interrupt, result.ExceptionHandleMode);

        var deserializedTrigger = Assert.IsType<TestTriggerProfile>(
            Assert.Single(result.TriggerProfiles));
        Assert.True(deserializedTrigger.ShouldTrigger);

        var deserializedAction = Assert.IsType<TestActionProfile>(
            Assert.Single(result.ActionProfiles));
        Assert.Equal(42, deserializedAction.RunDelayMilliseconds);

        Assert.Equal(RulesetMode.Any, result.RulesetProfile.Mode);
        var deserializedRule = Assert.IsType<TestRule>(
            Assert.Single(result.RulesetProfile.Rules));
        Assert.True(deserializedRule.IsReversed);
        Assert.Equal(7, deserializedRule.Profile.ValidateCount);
        Assert.True(deserializedRule.Profile.Result);
    }

    [Fact]
    public void SerializeWorkflow_WhenRuleIsUnregistered_Throws()
    {
        var workflow = TestWorkflows.Create(
            new TestTriggerProfile(),
            TestWorkflows.CreateRuleset(RulesetMode.All, new UnregisteredRule()));

        Assert.Throws<InvalidOperationException>(() => _storage.SerializeWorkflow(workflow));
    }

    [Fact]
    public void DeserializeWorkflow_WhenTypeIdentifierIsMissing_Throws()
    {
        var json = $$"""
                     {
                       "Guid": "{{Guid.NewGuid()}}",
                       "Name": "Invalid",
                       "TriggerProfiles": [ { "ShouldTrigger": true } ],
                       "RulesetProfile": { "Rules": [], "Mode": 0 },
                       "ActionProfiles": []
                     }
                     """;

        Assert.Throws<System.Text.Json.JsonException>(
            () => _storage.DeserializeWorkflow(json));
    }

    [Fact]
    public void DeserializeWorkflow_WhenTypeIdentifierIsUnknown_Throws()
    {
        var json = $$"""
                     {
                       "Guid": "{{Guid.NewGuid()}}",
                       "Name": "Invalid",
                       "TriggerProfiles": [ { "$type": "{{Guid.NewGuid()}}" } ],
                       "RulesetProfile": { "Rules": [], "Mode": 0 },
                       "ActionProfiles": []
                     }
                     """;

        Assert.Throws<System.Text.Json.JsonException>(
            () => _storage.DeserializeWorkflow(json));
    }

    [Fact]
    public async Task SaveLoadAndDeleteWorkflow_Works()
    {
        var workflow = CreatePersistableWorkflow();

        Assert.False(_storage.WorkflowExists(workflow.Guid));
        Assert.Null(await _storage.LoadWorkflowAsync(workflow.Guid));
        Assert.Empty(await _storage.LoadWorkflowsAsync());

        await _storage.SaveWorkflowAsync(workflow);

        var expectedFile = Path.Combine(_storagePath, $"{workflow.Guid:D}.json");
        Assert.True(File.Exists(expectedFile));
        Assert.True(_storage.WorkflowExists(workflow.Guid));

        var loaded = await _storage.LoadWorkflowAsync(workflow.Guid);
        Assert.NotNull(loaded);
        Assert.Equal(workflow.Guid, loaded.Guid);
        Assert.Equal(workflow.Name, loaded.Name);

        var all = await _storage.LoadWorkflowsAsync();
        Assert.Equal(workflow.Guid, Assert.Single(all).Guid);

        Assert.True(await _storage.DeleteWorkflowAsync(workflow.Guid));
        Assert.False(_storage.WorkflowExists(workflow.Guid));
        Assert.False(await _storage.DeleteWorkflowAsync(workflow.Guid));
        Assert.Null(await _storage.LoadWorkflowAsync(workflow.Guid));
    }

    [Fact]
    public async Task SaveWorkflowAsync_OverwritesExistingFile()
    {
        var workflow = CreatePersistableWorkflow();
        workflow.Name = "first";
        await _storage.SaveWorkflowAsync(workflow);

        workflow.Name = "second";
        await _storage.SaveWorkflowAsync(workflow);

        var loaded = await _storage.LoadWorkflowAsync(workflow.Guid);

        Assert.NotNull(loaded);
        Assert.Equal("second", loaded.Name);
        Assert.Single(Directory.EnumerateFiles(_storagePath, "*.json"));
    }

    [Fact]
    public async Task LoadWorkflowsAsync_WhenStorageDirectoryDoesNotExist_ReturnsEmpty()
    {
        Assert.False(Directory.Exists(_storagePath));

        var workflows = await _storage.LoadWorkflowsAsync();

        Assert.Empty(workflows);
    }

    [Fact]
    public async Task DeleteWorkflowAsync_WhenCancelled_Throws()
    {
        using var cancellationSource = new CancellationTokenSource();
        await cancellationSource.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => _storage.DeleteWorkflowAsync(Guid.NewGuid(), cancellationSource.Token));
    }

    private static Workflow CreatePersistableWorkflow()
    {
        var workflow = TestWorkflows.Create(
            new TestTriggerProfile { ShouldTrigger = true },
            TestWorkflows.CreateRuleset(
                RulesetMode.All,
                new TestRule { Profile = new TestRuleProfile { Result = true } }),
            [new TestActionProfile()],
            isReversionEnabled: true);
        workflow.Name = "Persisted workflow";
        return workflow;
    }
}
