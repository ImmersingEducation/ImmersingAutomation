using System.Collections.Concurrent;
using System.Diagnostics;
using ImmersingAutomation.Abstractions;
using ImmersingAutomation.Enums;
using ImmersingAutomation.Events;
using ImmersingAutomation.Models;
using Microsoft.Extensions.DependencyInjection;

namespace ImmersingAutomation;

public class AutomationService : IAsyncDisposable
{
    private readonly ConcurrentDictionary<TriggerBase, byte> _activeTriggers = new();
    private readonly ConcurrentDictionary<WorkflowRunner, byte> _reversionRunners = new();
    private readonly SemaphoreSlim _runnerLock = new(1, 1);
    private readonly CancellationTokenSource _lifetimeSource = new();

    private IServiceCollection _serviceCollection = new ServiceCollection();
    private IServiceProvider _serviceProvider;
    private IAutomationStorageService _storageService;
    private List<Workflow> _workflows = [];
    private List<WorkflowRunner> _runners = [];
    private Task? _triggerLoopTask;
    private Task? _reversionLoopTask;
    private int _triggerLoopRunning;
    private int _reversionLoopRunning;
    private int _disposed;

    public bool IsInitialized { get; private set; } = false;

    /// <summary>
    /// Interval between two polls of the registered triggers.
    /// </summary>
    public TimeSpan TriggerPollInterval { get; init; } = TimeSpan.FromMilliseconds(20);

    /// <summary>
    /// Interval between two checks of the rulesets that are waiting for reversion.
    /// </summary>
    public TimeSpan ReversionPollInterval { get; init; } = TimeSpan.FromMilliseconds(50);

    public IServiceCollection? ServiceCollection
    {
        get => !IsInitialized ? _serviceCollection : null;
        set => _serviceCollection = value ?? new ServiceCollection();
    }

    public IServiceProvider? ServiceProvider
    {
        get => IsInitialized ? _serviceProvider : null;
        set
        {
            if (value is null)
            {
                return;
            }

            _serviceProvider = value;
            IsInitialized = true;
        }
    }

    internal IEnumerable<ServiceDescriptor> ServiceDescriptors => _serviceCollection;

    public AutomationService(IAutomationStorageService storageService)
    {
        _storageService = storageService;
    }

    public async Task InitializeAsync()
    {
        if (IsInitialized)
        {
            return;
        }

        _serviceProvider = _serviceCollection.BuildServiceProvider();
        _workflows = (await _storageService.LoadWorkflowsAsync().ConfigureAwait(false)).ToList();

        foreach (var workflow in _workflows)
        {
            workflow.IsEnabledChanged += OnWorkflowIsEnabledChanged;
        }

        foreach (var workflow in _workflows.Where(workflow => workflow.IsEnabled))
        {
            await AddWorkflowRunner(workflow).ConfigureAwait(false);
        }

        StartTriggerQueryLoop();
        StartReversionQueryLoop();
        IsInitialized = true;
    }

    private void StartTriggerQueryLoop()
    {
        if (Interlocked.CompareExchange(ref _triggerLoopRunning, 1, 0) != 0)
        {
            return;
        }

        _triggerLoopTask = Task.Run(() => RunTriggerQueryLoopAsync(_lifetimeSource.Token));
    }

    /// <summary>
    /// Polls the registered triggers until <paramref name="cancellationToken"/> is cancelled.
    /// Only one loop can run at a time.
    /// </summary>
    public Task TriggerQueryLoop(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _triggerLoopRunning, 1, 0) != 0)
        {
            throw new InvalidOperationException("The trigger query loop is already running.");
        }

        return RunTriggerQueryLoopAsync(cancellationToken);
    }

    private async Task RunTriggerQueryLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TriggerPollInterval);

            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                // Keys returns a snapshot, so registering/unregistering during the loop is safe.
                foreach (var trigger in _activeTriggers.Keys)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    if (trigger.Status is not TriggerStatus.Ready)
                    {
                        continue;
                    }

                    try
                    {
                        // Trigger() validates internally; calling Validate() first would evaluate
                        // the condition twice, which is wrong for consuming triggers.
                        trigger.Trigger();
                    }
                    catch (Exception exception)
                    {
                        // A single faulty trigger must not stop the whole poll loop.
                        Debug.WriteLine(
                            $"[AutomationService] Trigger '{trigger.GetType().FullName}' failed: {exception}");
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected when the service is shutting down.
        }
        finally
        {
            Volatile.Write(ref _triggerLoopRunning, 0);
        }
    }

    public async Task StopTriggerQueryLoopAsync()
    {
        await _lifetimeSource.CancelAsync().ConfigureAwait(false);

        var task = _triggerLoopTask;
        if (task is null)
        {
            return;
        }

        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected when the service is shutting down.
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
        }

        _triggerLoopTask = null;
    }

    private void StartReversionQueryLoop()
    {
        if (Interlocked.CompareExchange(ref _reversionLoopRunning, 1, 0) != 0)
        {
            return;
        }

        _reversionLoopTask = Task.Run(() => RunReversionQueryLoopAsync(_lifetimeSource.Token));
    }

    /// <summary>
    /// Polls the rulesets of the runners that are waiting for reversion and starts the reversion
    /// as soon as their ruleset no longer matches. Only one loop can run at a time.
    /// </summary>
    public Task ReversionQueryLoop(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _reversionLoopRunning, 1, 0) != 0)
        {
            throw new InvalidOperationException("The reversion query loop is already running.");
        }

        return RunReversionQueryLoopAsync(cancellationToken);
    }

    private async Task RunReversionQueryLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(ReversionPollInterval);

            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                // Keys returns a snapshot, so registering/unregistering during the loop is safe.
                foreach (var runner in _reversionRunners.Keys)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    if (runner.Status is not WorkflowRunnerStatus.WaitingForReversion)
                    {
                        _reversionRunners.TryRemove(runner, out _);
                        continue;
                    }

                    bool rulesetMatches;
                    try
                    {
                        rulesetMatches = await runner.Ruleset.ValidateAsync().ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        // A faulty ruleset must not stop the loop; keep polling and try again.
                        Debug.WriteLine(
                            $"[AutomationService] Ruleset of runner '{runner.Guid}' failed: {exception}");
                        continue;
                    }

                    if (rulesetMatches)
                    {
                        continue;
                    }

                    // Remove before dispatching so the same waiting period reversion is started once.
                    if (_reversionRunners.TryRemove(runner, out _))
                    {
                        TriggerReversion(runner);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected when the service is shutting down.
        }
        finally
        {
            Volatile.Write(ref _reversionLoopRunning, 0);
        }
    }

    private void TriggerReversion(WorkflowRunner runner)
    {
        // Do not await here: reverting may take long and must not block the poll loop for the
        // other runners. OnReversionTriggered re-checks the runner status itself.
        _ = Task.Run(async () =>
        {
            try
            {
                await runner.OnReversionTriggered(this, EventArgs.Empty).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                Debug.WriteLine(
                    $"[AutomationService] Reversion of runner '{runner.Guid}' failed: {exception}");
            }
        });
    }

    public async Task StopReversionQueryLoopAsync()
    {
        await _lifetimeSource.CancelAsync().ConfigureAwait(false);

        var task = _reversionLoopTask;
        if (task is null)
        {
            return;
        }

        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected when the service is shutting down.
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
        }

        _reversionLoopTask = null;
    }

    public async void OnWorkflowIsEnabledChanged(object? sender, EventArgs e)
    {
        try
        {
            if (sender is not Workflow workflow)
            {
                return;
            }

            if (workflow.IsEnabled)
            {
                await AddWorkflowRunner(workflow).ConfigureAwait(false);
            }
            else
            {
                await RemoveWorkflowRunner(workflow).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            // The event is raised from a property setter; an escaping exception would crash the process.
            Debug.WriteLine(exception);
        }
    }

    public async Task AddWorkflowRunner(Workflow workflow)
    {
        ArgumentNullException.ThrowIfNull(workflow);

        WorkflowRunner runner;

        await _runnerLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_runners.Any(x => x.Workflow.Guid == workflow.Guid))
            {
                return;
            }

            runner = new WorkflowRunner(workflow);
            _runners.Add(runner);
            runner.StatusChanged += OnWorkflowRunnerStatusChanged;
        }
        finally
        {
            _runnerLock.Release();
        }

        try
        {
            await runner.InitializeAsync().ConfigureAwait(false);

            // Register only after initialization succeeds: on failure the runner clears its
            // Triggers collection, so the triggers could no longer be found for unregistration.
            // The lock prevents a concurrent RemoveWorkflowRunner from disposing the runner
            // between the check and the registration, which would leave stale triggers behind.
            await _runnerLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_runners.Contains(runner))
                {
                    RegisterTriggers(runner);
                }
            }
            finally
            {
                _runnerLock.Release();
            }
        }
        catch
        {
            await _runnerLock.WaitAsync().ConfigureAwait(false);
            try
            {
                _runners.Remove(runner);
            }
            finally
            {
                _runnerLock.Release();
            }

            runner.StatusChanged -= OnWorkflowRunnerStatusChanged;
            UnregisterTriggers(runner);

            try
            {
                await runner.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                Debug.WriteLine(exception);
            }

            throw;
        }
    }

    public async Task RemoveWorkflowRunner(Workflow workflow)
    {
        WorkflowRunner? runner;

        await _runnerLock.WaitAsync().ConfigureAwait(false);
        try
        {
            runner = _runners.FirstOrDefault(x => x.Workflow.Guid == workflow.Guid);
            if (runner is null)
            {
                return;
            }

            _runners.Remove(runner);
        }
        finally
        {
            _runnerLock.Release();
        }

        try
        {
            await runner.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            // DisposeAsync raises Destroying, but make sure nothing stays registered if it throws.
            UnregisterTriggers(runner);
            runner.StatusChanged -= OnWorkflowRunnerStatusChanged;
        }
    }

    public Task ExternallyInitializeWorkflowRunnerAsync(WorkflowRunner runner)
    {
        ArgumentNullException.ThrowIfNull(runner);

        RegisterTriggers(runner);
        return Task.CompletedTask;
    }

    public void OnWorkflowRunnerStatusChanged(object? sender, WorkflowRunnerStatusChangedEventArgs e)
    {
        if (sender is not WorkflowRunner runner)
        {
            return;
        }

        // The runner is only watched while it is waiting for its actions to be reverted.
        if (e.Status is WorkflowRunnerStatus.WaitingForReversion)
        {
            _reversionRunners.TryAdd(runner, 0);
        }
        else
        {
            _reversionRunners.TryRemove(runner, out _);
        }

        switch (e.Status)
        {
            case WorkflowRunnerStatus.Destroying:
                // The runner clears its Triggers after raising Destroying, so unregister now.
                UnregisterTriggers(runner);
                break;
            default:
                return;
        }
    }

    private void RegisterTriggers(WorkflowRunner runner)
    {
        foreach (var trigger in runner.Triggers)
        {
            _activeTriggers.TryAdd(trigger, 0);
        }
    }

    private void UnregisterTriggers(WorkflowRunner runner)
    {
        foreach (var trigger in runner.Triggers)
        {
            _activeTriggers.TryRemove(trigger, out _);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await StopTriggerQueryLoopAsync().ConfigureAwait(false);
        await StopReversionQueryLoopAsync().ConfigureAwait(false);

        await _runnerLock.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var runner in _runners.ToArray())
            {
                runner.StatusChanged -= OnWorkflowRunnerStatusChanged;

                try
                {
                    await runner.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    Debug.WriteLine(exception);
                }
            }

            _runners.Clear();
        }
        finally
        {
            _runnerLock.Release();
        }

        foreach (var workflow in _workflows)
        {
            workflow.IsEnabledChanged -= OnWorkflowIsEnabledChanged;
        }

        _workflows.Clear();
        _activeTriggers.Clear();
        _reversionRunners.Clear();
        _lifetimeSource.Dispose();
        _runnerLock.Dispose();
    }
}
