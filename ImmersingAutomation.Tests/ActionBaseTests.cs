using ImmersingAutomation.Enums;

namespace ImmersingAutomation.Tests;

public class ActionBaseTests
{
    [Fact]
    public async Task RunAsync_WithoutReversion_ReturnsToIdle()
    {
        var action = CreateAction(new TestActionProfile());

        await action.RunAsync(isReversionNeeded: false);

        Assert.Equal(ActionStatus.WaitingForRunning, action.Status);
        Assert.Equal(1, action.RunCount);
    }

    [Fact]
    public async Task RunAsync_WithReversion_WaitsForRevert()
    {
        var action = CreateAction(new TestActionProfile());

        await action.RunAsync(isReversionNeeded: true);

        Assert.Equal(ActionStatus.WaitingForReversion, action.Status);
        Assert.Equal(1, action.RunCount);

        await action.RevertAsync();

        Assert.Equal(ActionStatus.WaitingForRunning, action.Status);
        Assert.Equal(1, action.RevertCount);
    }

    [Fact]
    public async Task RunAsync_WhileWaitingForReversion_Throws()
    {
        var action = CreateAction(new TestActionProfile());
        await action.RunAsync(isReversionNeeded: true);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => action.RunAsync(isReversionNeeded: false));
    }

    [Fact]
    public async Task RevertAsync_WhenNotWaitingForReversion_Throws()
    {
        var action = CreateAction(new TestActionProfile());

        await Assert.ThrowsAsync<InvalidOperationException>(() => action.RevertAsync());
    }

    [Fact]
    public async Task Reset_WhenWaitingForReversion_ReturnsToIdle()
    {
        var action = CreateAction(new TestActionProfile());
        await action.RunAsync(isReversionNeeded: true);

        action.Reset();

        Assert.Equal(ActionStatus.WaitingForRunning, action.Status);
    }

    [Fact]
    public async Task Reset_WhenIdle_Throws()
    {
        var action = CreateAction(new TestActionProfile());

        Assert.Throws<InvalidOperationException>(() => action.Reset());
    }

    [Fact]
    public void Interrupt_WhenIdle_ReturnsFalse()
    {
        var action = CreateAction(new TestActionProfile());

        Assert.False(action.Interrupt());
    }

    [Fact]
    public async Task Interrupt_WhileRunning_CancelsRunAndWaitsForReversion()
    {
        var profile = new TestActionProfile
        {
            RunStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            RunGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var action = CreateAction(profile);

        var runTask = action.RunAsync(isReversionNeeded: true);
        await profile.RunStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(ActionStatus.Running, action.Status);
        Assert.True(action.Interrupt());

        await runTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(ActionStatus.WaitingForReversion, action.Status);
        Assert.Equal(1, action.InterruptionObservedCount);
    }

    [Fact]
    public async Task Interrupt_WhileWaitingForReversion_ReturnsFalse()
    {
        var action = CreateAction(new TestActionProfile());
        await action.RunAsync(isReversionNeeded: true);

        Assert.False(action.Interrupt());
    }

    [Fact]
    public async Task Interrupt_WhileReverting_CancelsRevert()
    {
        var profile = new TestActionProfile
        {
            RevertStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            RevertGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var action = CreateAction(profile);
        await action.RunAsync(isReversionNeeded: true);

        var revertTask = action.RevertAsync();
        await profile.RevertStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(ActionStatus.Reverting, action.Status);
        Assert.True(action.Interrupt());

        await revertTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(ActionStatus.WaitingForRunning, action.Status);
        Assert.Equal(1, action.InterruptionObservedCount);
    }

    [Fact]
    public async Task ForceReset_WhileRunning_CancelsAndReturnsToIdle()
    {
        var profile = new TestActionProfile
        {
            RunStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            RunGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var action = CreateAction(profile);

        var runTask = action.RunAsync(isReversionNeeded: true);
        await profile.RunStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        action.ForceReset();

        await runTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(ActionStatus.WaitingForRunning, action.Status);
        Assert.Equal(1, action.InterruptionObservedCount);
    }

    [Fact]
    public void ForceReset_WhenIdle_DoesNothing()
    {
        var action = CreateAction(new TestActionProfile());

        action.ForceReset();

        Assert.Equal(ActionStatus.WaitingForRunning, action.Status);
    }

    [Fact]
    public async Task RunAsync_WhenActionThrows_WithReversion_LeavesActionWaitingForReversion()
    {
        var profile = new TestActionProfile
        {
            RunException = new InvalidOperationException("run failed")
        };
        var action = CreateAction(profile);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => action.RunAsync(isReversionNeeded: true));

        Assert.Equal(ActionStatus.WaitingForReversion, action.Status);
    }

    [Fact]
    public async Task RunAsync_WhenActionThrows_WithoutReversion_ReturnsToIdle()
    {
        var profile = new TestActionProfile
        {
            RunException = new InvalidOperationException("run failed")
        };
        var action = CreateAction(profile);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => action.RunAsync(isReversionNeeded: false));

        Assert.Equal(ActionStatus.WaitingForRunning, action.Status);
    }

    private static TestAction CreateAction(TestActionProfile profile)
    {
        return new TestAction { Profile = profile };
    }
}
