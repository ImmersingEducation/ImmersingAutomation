using ImmersingAutomation.Enums;

namespace ImmersingAutomation.Tests;

public class TriggerBaseTests
{
    [Fact]
    public void Trigger_WhenNotInitialized_ReturnsFalse()
    {
        var trigger = CreateTrigger(profile => profile.ShouldTrigger = true);
        var raised = 0;
        trigger.Triggered += (_, _) => raised++;

        Assert.False(trigger.Trigger());
        Assert.Equal(TriggerStatus.Created, trigger.Status);
        Assert.Equal(0, raised);
    }

    [Fact]
    public async Task Trigger_WhenValidateReturnsFalse_ReturnsFalse()
    {
        var profile = new TestTriggerProfile { ShouldTrigger = false };
        var trigger = CreateTrigger(profile);
        var raised = 0;
        trigger.Triggered += (_, _) => raised++;

        await trigger.InitializeAsync();

        Assert.Equal(TriggerStatus.Ready, trigger.Status);
        Assert.False(trigger.Trigger());
        Assert.Equal(1, profile.ValidateCount);
        Assert.Equal(0, raised);
    }

    [Fact]
    public async Task Trigger_WhenReadyAndValidateReturnsTrue_RaisesTriggered()
    {
        var profile = new TestTriggerProfile { ShouldTrigger = true };
        var trigger = CreateTrigger(profile);
        var raised = 0;
        trigger.Triggered += (_, _) => raised++;

        await trigger.InitializeAsync();

        Assert.True(trigger.Trigger());
        Assert.Equal(1, profile.ValidateCount);
        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task InitializeAsync_WhenAlreadyInitialized_IsIdempotent()
    {
        var profile = new TestTriggerProfile();
        var trigger = CreateTrigger(profile);

        await trigger.InitializeAsync();
        await trigger.InitializeAsync();

        Assert.Equal(TriggerStatus.Ready, trigger.Status);
        Assert.Equal(1, profile.InitializeCount);
    }

    [Fact]
    public async Task InitializeAsync_WhenUnderlyingInitializationFails_SetsFaulted()
    {
        var profile = new TestTriggerProfile { ThrowOnInitialize = true };
        var trigger = CreateTrigger(profile);

        await Assert.ThrowsAsync<InvalidOperationException>(() => trigger.InitializeAsync());

        Assert.Equal(TriggerStatus.Faulted, trigger.Status);
        Assert.Equal(0, profile.InitializeCount);

        profile.ThrowOnInitialize = false;
        await trigger.InitializeAsync();

        Assert.Equal(TriggerStatus.Ready, trigger.Status);
        Assert.Equal(1, profile.InitializeCount);
    }

    [Fact]
    public async Task ReleaseAsync_WhenReady_ReleasesOnce()
    {
        var profile = new TestTriggerProfile();
        var trigger = CreateTrigger(profile);
        await trigger.InitializeAsync();

        await trigger.ReleaseAsync();

        Assert.Equal(TriggerStatus.Released, trigger.Status);
        Assert.Equal(1, profile.ReleaseCount);

        await trigger.ReleaseAsync();

        Assert.Equal(TriggerStatus.Released, trigger.Status);
        Assert.Equal(1, profile.ReleaseCount);
    }

    [Fact]
    public async Task ReleaseAsync_WhenCreated_DoesNothing()
    {
        var profile = new TestTriggerProfile();
        var trigger = CreateTrigger(profile);

        await trigger.ReleaseAsync();

        Assert.Equal(TriggerStatus.Created, trigger.Status);
        Assert.Equal(0, profile.ReleaseCount);
    }

    [Fact]
    public async Task ReleaseAsync_WhenUnderlyingReleaseFails_SetsFaulted()
    {
        var profile = new TestTriggerProfile { ThrowOnRelease = true };
        var trigger = CreateTrigger(profile);
        await trigger.InitializeAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => trigger.ReleaseAsync());

        Assert.Equal(TriggerStatus.Faulted, trigger.Status);

        profile.ThrowOnRelease = false;
        await trigger.ReleaseAsync();

        Assert.Equal(TriggerStatus.Released, trigger.Status);
        Assert.Equal(1, profile.ReleaseCount);
    }

    [Fact]
    public async Task InitializeAsync_AfterRelease_Reinitializes()
    {
        var profile = new TestTriggerProfile();
        var trigger = CreateTrigger(profile);
        await trigger.InitializeAsync();
        await trigger.ReleaseAsync();

        await trigger.InitializeAsync();

        Assert.Equal(TriggerStatus.Ready, trigger.Status);
        Assert.Equal(2, profile.InitializeCount);
    }

    [Fact]
    public async Task DisposeAsync_ReleasesAndDisposes()
    {
        var profile = new TestTriggerProfile();
        var trigger = CreateTrigger(profile);
        await trigger.InitializeAsync();

        await trigger.DisposeAsync();

        Assert.Equal(TriggerStatus.Disposed, trigger.Status);
        Assert.Equal(1, profile.ReleaseCount);

        await trigger.DisposeAsync();

        Assert.Equal(TriggerStatus.Disposed, trigger.Status);
        Assert.Equal(1, profile.ReleaseCount);
    }

    [Fact]
    public async Task DisposeAsync_WhenNeverInitialized_DoesNotRelease()
    {
        var profile = new TestTriggerProfile();
        var trigger = CreateTrigger(profile);

        await trigger.DisposeAsync();

        Assert.Equal(TriggerStatus.Disposed, trigger.Status);
        Assert.Equal(0, profile.ReleaseCount);
    }

    [Fact]
    public async Task InitializeAsync_WhenDisposed_ThrowsObjectDisposedException()
    {
        var trigger = CreateTrigger(new TestTriggerProfile());
        await trigger.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => trigger.InitializeAsync());
    }

    private static TestTrigger CreateTrigger(TestTriggerProfile profile)
    {
        return new TestTrigger { Profile = profile };
    }

    private static TestTrigger CreateTrigger(Action<TestTriggerProfile> configure)
    {
        var profile = new TestTriggerProfile();
        configure(profile);
        return CreateTrigger(profile);
    }
}
