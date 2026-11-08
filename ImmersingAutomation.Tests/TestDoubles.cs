using System.Text.Json.Serialization;
using ImmersingAutomation.Abstractions;
using ImmersingAutomation.Attributes;

namespace ImmersingAutomation.Tests;

[TriggerProfile(typeof(TestTrigger))]
public sealed class TestTriggerProfile : TriggerProfileBase
{
    public bool ShouldTrigger { get; set; }

    public bool ThrowOnInitialize { get; set; }

    public bool ThrowOnRelease { get; set; }

    public int InitializeCount { get; set; }

    public int ReleaseCount { get; set; }

    public int ValidateCount { get; set; }

    [JsonIgnore]
    public Func<bool>? Validator { get; set; }

    [JsonIgnore]
    public TaskCompletionSource? Initialized { get; set; }

    [JsonIgnore]
    public TaskCompletionSource? Released { get; set; }
}

[Trigger("6f9619ff-8b86-d011-b42d-00cf4fc964ff", "Test trigger")]
public sealed class TestTrigger : TriggerBase<TestTriggerProfile>
{
    public override bool Validate()
    {
        Profile.ValidateCount++;
        return Profile.Validator?.Invoke() ?? Profile.ShouldTrigger;
    }

    protected override Task _InitializeAsync()
    {
        if (Profile.ThrowOnInitialize)
        {
            throw new InvalidOperationException("The test trigger failed to initialize.");
        }

        Profile.InitializeCount++;
        Profile.Initialized?.TrySetResult();

        return Task.CompletedTask;
    }

    protected override Task _ReleaseAsync()
    {
        if (Profile.ThrowOnRelease)
        {
            throw new InvalidOperationException("The test trigger failed to release.");
        }

        Profile.ReleaseCount++;
        Profile.Released?.TrySetResult();

        return Task.CompletedTask;
    }
}

[RuleProfile(typeof(TestRule))]
public sealed class TestRuleProfile : RuleProfileBase
{
    public bool Result { get; set; }

    public int ValidateCount { get; set; }

    [JsonIgnore]
    public Func<bool>? Validator { get; set; }
}

[Rule("7f9619ff-8b86-d011-b42d-00cf4fc964ff", "Test rule")]
public sealed class TestRule : RuleBase<TestRuleProfile>
{
    protected override Task<bool> _ValidateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Profile.ValidateCount++;
        return Task.FromResult(Profile.Validator?.Invoke() ?? Profile.Result);
    }
}

[ActionProfile(typeof(TestAction))]
public sealed class TestActionProfile : ActionProfileBase
{
    public int RunDelayMilliseconds { get; set; }

    public int RunCount { get; set; }

    public int RevertCount { get; set; }

    public int InterruptionObservedCount { get; set; }

    [JsonIgnore]
    public Exception? RunException { get; set; }

    [JsonIgnore]
    public Exception? RevertException { get; set; }

    [JsonIgnore]
    public TaskCompletionSource? RunStarted { get; set; }

    [JsonIgnore]
    public TaskCompletionSource? RunGate { get; set; }

    [JsonIgnore]
    public TaskCompletionSource? RevertStarted { get; set; }

    [JsonIgnore]
    public TaskCompletionSource? RevertGate { get; set; }

    [JsonIgnore]
    public TaskCompletionSource? RevertCompleted { get; set; }

    [JsonIgnore]
    public Action? RunCompleted { get; set; }
}

[Action("8f9619ff-8b86-d011-b42d-00cf4fc964ff", "Test action", isReversionSupported: true)]
public sealed class TestAction : ActionBase<TestActionProfile>
{
    public int RunCount { get; private set; }

    public int RevertCount { get; private set; }

    public int InterruptionObservedCount { get; private set; }

    protected override async Task _RunAsync()
    {
        RunCount++;
        Profile.RunCount++;
        Profile.RunStarted?.TrySetResult();

        try
        {
            if (Profile.RunGate is not null)
            {
                await Profile.RunGate.Task.WaitAsync(InterruptionToken);
            }

            if (Profile.RunDelayMilliseconds > 0)
            {
                await Task.Delay(Profile.RunDelayMilliseconds, InterruptionToken);
            }
        }
        catch (OperationCanceledException)
        {
            InterruptionObservedCount++;
            Profile.InterruptionObservedCount++;
            throw;
        }

        if (Profile.RunException is not null)
        {
            throw Profile.RunException;
        }

        Profile.RunCompleted?.Invoke();
    }

    protected override async Task _RevertAsync()
    {
        RevertCount++;
        Profile.RevertCount++;
        Profile.RevertStarted?.TrySetResult();

        try
        {
            if (Profile.RevertGate is not null)
            {
                await Profile.RevertGate.Task.WaitAsync(InterruptionToken);
            }
        }
        catch (OperationCanceledException)
        {
            InterruptionObservedCount++;
            Profile.InterruptionObservedCount++;
            throw;
        }

        if (Profile.RevertException is not null)
        {
            throw Profile.RevertException;
        }

        Profile.RevertCompleted?.TrySetResult();
    }
}

public sealed class UnattributedTriggerProfile : TriggerProfileBase
{
}

public sealed class UnattributedActionProfile : ActionProfileBase
{
}

public sealed class UnregisteredRule : RuleBase
{
    protected override Task<bool> _ValidateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(true);
    }
}

[TriggerProfile(typeof(OtherTrigger))]
public sealed class MismatchedTriggerProfile : TriggerProfileBase
{
}

public sealed class OtherTrigger : TriggerBase<TestTriggerProfile>
{
    protected override Task _InitializeAsync()
    {
        return Task.CompletedTask;
    }

    protected override Task _ReleaseAsync()
    {
        return Task.CompletedTask;
    }
}

[ActionProfile(typeof(OtherAction))]
public sealed class MismatchedActionProfile : ActionProfileBase
{
}

public sealed class OtherAction : ActionBase<TestActionProfile>
{
    protected override Task _RunAsync()
    {
        return Task.CompletedTask;
    }

    protected override Task _RevertAsync()
    {
        return Task.CompletedTask;
    }
}

[ActionProfile(typeof(NonReversibleAction))]
public sealed class NonReversibleActionProfile : ActionProfileBase
{
}

[Action("9f9619ff-8b86-d011-b42d-00cf4fc964ff", "Non-reversible action", isReversionSupported: false)]
public sealed class NonReversibleAction : ActionBase<NonReversibleActionProfile>
{
    protected override Task _RunAsync()
    {
        return Task.CompletedTask;
    }

    protected override Task _RevertAsync()
    {
        return Task.CompletedTask;
    }
}

public sealed class TestDependency
{
    public int Value { get; } = 42;
}

[TriggerProfile(typeof(DependencyTrigger))]
public sealed class DependencyTriggerProfile : TriggerProfileBase
{
}

[Trigger("af9619ff-8b86-d011-b42d-00cf4fc964ff", "Dependency trigger")]
public sealed class DependencyTrigger : TriggerBase<DependencyTriggerProfile>
{
    public DependencyTrigger(TestDependency dependency)
    {
        Dependency = dependency;
    }

    public TestDependency Dependency { get; }

    protected override Task _InitializeAsync()
    {
        return Task.CompletedTask;
    }

    protected override Task _ReleaseAsync()
    {
        return Task.CompletedTask;
    }
}

public sealed class DisposableSingleton : IDisposable
{
    public bool IsDisposed { get; private set; }

    public void Dispose()
    {
        IsDisposed = true;
    }
}
