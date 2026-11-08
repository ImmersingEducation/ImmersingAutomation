using System.Diagnostics;
using System.Reflection;
using ImmersingAutomation.Abstractions;
using ImmersingAutomation.Attributes;
using ImmersingAutomation.Enums;
using ImmersingAutomation.Events;

namespace ImmersingAutomation.Models;

public class WorkflowRunner : IAsyncDisposable
{
    public bool Initialized { get; private set; } = false;

    public Guid Guid { get; } = Guid.NewGuid();

    public Workflow Workflow { get; }

    public WorkflowRunnerStatus Status
    {
        get
        {
            lock (_statusLock)
            {
                return _status;
            }
        }
    }

    private readonly List<TriggerBase> _triggers = [];

    private readonly List<ActionBase> _actions = [];

    public IReadOnlyList<TriggerBase> Triggers { get; }

    public Ruleset Ruleset { get; private set; } = null!;

    public IReadOnlyList<ActionBase> Actions { get; }

    public event EventHandler<WorkflowRunnerStatusChangedEventArgs>? StatusChanged;

    private readonly SemaphoreSlim _triggerLock = new(1, 1);

    private readonly SemaphoreSlim _initializeLock = new(1, 1);

    private readonly object _statusLock = new();

    private readonly HashSet<ActionBase> _skippedActions = [];

    private WorkflowRunnerStatus _status;

    private ActionBase? _currentAction;

    private int _executedActionCount;

    private bool _interruptionRequested;

    private bool _isReversionEnabled;

    private WorkflowRunnerExceptionHandleMode _exceptionHandleMode = WorkflowRunnerExceptionHandleMode.Skip;

    private bool _disposed;

    public WorkflowRunner(Workflow workflow)
    {
        Workflow = workflow ?? throw new ArgumentNullException(nameof(workflow));
        Triggers = _triggers.AsReadOnly();
        Actions = _actions.AsReadOnly();
    }

    /// <inheritdoc cref="InitializeAsync"/>
    public void Initialize()
    {
        Task.Run(InitializeAsync).GetAwaiter().GetResult();
    }

    public async Task InitializeAsync()
    {
        try
        {
            await _initializeLock.WaitAsync();
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (Initialized) return;

            OnStatusChanged(WorkflowRunnerStatus.Initializing);

            _triggers.Clear();
            _actions.Clear();
            Ruleset = null!;

            try
            {
                foreach (var triggerProfile in Workflow.TriggerProfiles)
                {
                    var profileType = triggerProfile.GetType();
                    var profileAttr = profileType.GetCustomAttribute<TriggerProfileAttribute>()
                        ?? throw new InvalidOperationException(
                            $"Trigger profile type '{profileType.FullName}' has no [{nameof(TriggerProfileAttribute)}].");

                    var expectedTriggerType = typeof(TriggerBase<>).MakeGenericType(profileType);
                    if (!expectedTriggerType.IsAssignableFrom(profileAttr.TriggerType))
                    {
                        throw new InvalidOperationException(
                            $"Trigger type '{profileAttr.TriggerType.FullName}' cannot use profile type " +
                            $"'{profileType.FullName}'.");
                    }

                    var trigger = Activator.CreateInstance(profileAttr.TriggerType) as TriggerBase
                        ?? throw new InvalidOperationException(
                            $"Failed to create trigger type '{profileAttr.TriggerType.FullName}'.");

                    SetProfile(trigger, triggerProfile);
                    _triggers.Add(trigger);
                    trigger.Triggered += OnTriggered;
                }

                Ruleset = new Ruleset { Profile = Workflow.RulesetProfile };

                foreach (var actionProfile in Workflow.ActionProfiles)
                {
                    var profileType = actionProfile.GetType();
                    var profileAttr = profileType.GetCustomAttribute<ActionProfileAttribute>()
                        ?? throw new InvalidOperationException(
                            $"Action profile type '{profileType.FullName}' has no [{nameof(ActionProfileAttribute)}].");

                    var expectedActionType = typeof(ActionBase<>).MakeGenericType(profileType);
                    if (!expectedActionType.IsAssignableFrom(profileAttr.ActionType))
                    {
                        throw new InvalidOperationException(
                            $"Action type '{profileAttr.ActionType.FullName}' cannot use profile type " +
                            $"'{profileType.FullName}'.");
                    }

                    var action = Activator.CreateInstance(profileAttr.ActionType) as ActionBase
                        ?? throw new InvalidOperationException(
                            $"Failed to create action type '{profileAttr.ActionType.FullName}'.");

                    SetProfile(action, actionProfile);
                    _actions.Add(action);
                }

                OnStatusChanged(WorkflowRunnerStatus.ExternalInitializing);

                foreach (var trigger in _triggers)
                {
                    await trigger.InitializeAsync();
                }

                Initialized = true;
                OnStatusChanged(WorkflowRunnerStatus.WaitingForTriggering);
            }
            catch
            {
                foreach (var trigger in _triggers.ToArray())
                {
                    trigger.Triggered -= OnTriggered;

                    try
                    {
                        await trigger.DisposeAsync();
                    }
                    catch (Exception exception)
                    {
                        Debug.WriteLine(exception);
                    }
                }

                _triggers.Clear();
                _actions.Clear();
                Ruleset = null!;
                Initialized = false;
                throw;
            }
        }
        finally
        {
            _initializeLock.Release();
        }
    }

    public async void OnTriggered(object? sender, EventArgs eventArgs)
    {
        if (_disposed || Status is not WorkflowRunnerStatus.WaitingForTriggering) return;

        // Serialize trigger handling so concurrent triggers cannot run the ruleset and actions at once.
        try
        {
            await _triggerLock.WaitAsync();
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            // Re-check after acquiring the lock: the status may have changed while waiting.
            if (_disposed || Status is not WorkflowRunnerStatus.WaitingForTriggering) return;

            lock (_statusLock)
            {
                // Snapshot the workflow settings so later mutations cannot affect this run.
                _isReversionEnabled = Workflow.IsReversionEnabled;
                _exceptionHandleMode = Workflow.ExceptionHandleMode;
            }

            OnStatusChanged(WorkflowRunnerStatus.VerifyingRuleset);
            var rulesetResult = await Ruleset.ValidateAsync();

            if (!rulesetResult)
            {
                var rulesetInterrupted = false;
                lock (_statusLock)
                {
                    rulesetInterrupted = _interruptionRequested;
                    if (!rulesetInterrupted)
                    {
                        _status = WorkflowRunnerStatus.WaitingForTriggering;
                    }
                }

                if (rulesetInterrupted)
                {
                    CompleteInterruption();
                }
                else
                {
                    RaiseStatusChanged(WorkflowRunnerStatus.WaitingForTriggering);
                }

                return;
            }

            var runInterrupted = false;
            lock (_statusLock)
            {
                runInterrupted = _interruptionRequested;

                if (!runInterrupted)
                {
                    _currentAction = null;
                    _executedActionCount = 0;
                    _skippedActions.Clear();
                    _status = WorkflowRunnerStatus.Running;
                }
            }

            if (runInterrupted)
            {
                CompleteInterruption();
                return;
            }

            RaiseStatusChanged(WorkflowRunnerStatus.Running);

            for (var i = 0; i < _actions.Count; i++)
            {
                if (IsInterruptionRequested) break;

                ActionBase action;
                lock (_statusLock)
                {
                    action = _actions[i];
                    _currentAction = action;
                }

                try
                {
                    await action.RunAsync(_isReversionEnabled);
                }
                catch (Exception) when (
                    _exceptionHandleMode is WorkflowRunnerExceptionHandleMode.Skip)
                {
                    // Skip mode: remember the failed action so reversion leaves it alone, and keep
                    // running the remaining actions.
                    if (_isReversionEnabled)
                    {
                        lock (_statusLock)
                        {
                            _skippedActions.Add(action);
                        }

                        action.ForceReset();
                    }
                }
                catch (Exception) when (
                    _exceptionHandleMode is WorkflowRunnerExceptionHandleMode.Interrupt)
                {
                    // Interrupt mode: stop the remaining actions and recover from the failed action.
                    RequestInterruption();
                }
                finally
                {
                    lock (_statusLock)
                    {
                        _currentAction = null;
                    }
                }

                // The current action has either completed or has been interrupted and is now
                // waiting for reversion, so it is part of the actions that may need reverting.
                lock (_statusLock)
                {
                    _executedActionCount = i + 1;
                }
            }

            if (IsInterruptionRequested)
            {
                await RevertAfterInterruptionAsync();
                return;
            }

            if (!_isReversionEnabled)
            {
                OnStatusChanged(WorkflowRunnerStatus.WaitingForTriggering);
                return;
            }

            var interrupted = false;
            lock (_statusLock)
            {
                interrupted = _interruptionRequested;
                if (!interrupted)
                {
                    _status = WorkflowRunnerStatus.WaitingForReversion;
                }
            }

            if (interrupted)
            {
                await RevertAfterInterruptionAsync();
            }
            else
            {
                RaiseStatusChanged(WorkflowRunnerStatus.WaitingForReversion);
            }
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
            ResetToIdle();
        }
        finally
        {
            try
            {
                _triggerLock.Release();
            }
            catch (ObjectDisposedException)
            {
                // The runner was disposed while the trigger handler was finishing.
            }
        }
    }

    /// <summary>
    /// Requests the current run or reversion to be interrupted.
    /// When running, reversion starts from the action that was running. When reverting, all
    /// remaining reversions are cancelled and the action state machine is reset.
    /// Returns <see langword="false"/> when the workflow runner cannot be interrupted now.
    /// </summary>
    public bool Interrupt()
    {
        ActionBase? currentAction;
        bool resetImmediately;

        lock (_statusLock)
        {
            switch (_status)
            {
                case WorkflowRunnerStatus.VerifyingRuleset:
                case WorkflowRunnerStatus.Running:
                case WorkflowRunnerStatus.Reverting:
                    _interruptionRequested = true;
                    _status = WorkflowRunnerStatus.Interrupting;
                    currentAction = _currentAction;
                    resetImmediately = false;
                    break;

                case WorkflowRunnerStatus.WaitingForReversion:
                    _interruptionRequested = true;
                    _status = WorkflowRunnerStatus.Interrupting;
                    currentAction = null;
                    resetImmediately = true;
                    break;

                default:
                    return false;
            }
        }

        RaiseStatusChanged(WorkflowRunnerStatus.Interrupting);

        if (resetImmediately)
        {
            // No reversion loop is running, so cancel the pending reversion right here and
            // return every action to the idle state without invoking their reversion.
            ResetAllActions();
            CompleteInterruption();
            return true;
        }

        currentAction?.Interrupt();
        return true;
    }

    public async Task OnReversionTriggered(object? sender, EventArgs eventArgs)
    {
        bool reversionEnabled;
        lock (_statusLock)
        {
            reversionEnabled = _isReversionEnabled;
        }

        if (_disposed || !reversionEnabled) return;
        if (Status is not WorkflowRunnerStatus.WaitingForReversion) return;

        try
        {
            await _triggerLock.WaitAsync();
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            if (_disposed || Status is not WorkflowRunnerStatus.WaitingForReversion) return;

            int executedActionCount;
            lock (_statusLock)
            {
                executedActionCount = _executedActionCount;
            }

            if (executedActionCount == 0)
            {
                OnStatusChanged(WorkflowRunnerStatus.WaitingForTriggering);
                return;
            }

            await StartReversionAsync(executedActionCount - 1);
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
            ResetToIdle();
        }
        finally
        {
            try
            {
                _triggerLock.Release();
            }
            catch (ObjectDisposedException)
            {
                // The runner was disposed while the reversion handler was finishing.
            }
        }
    }

    private async Task RevertAfterInterruptionAsync()
    {
        int executedActionCount;

        lock (_statusLock)
        {
            if (!_isReversionEnabled || _executedActionCount == 0)
            {
                executedActionCount = 0;
            }
            else
            {
                executedActionCount = _executedActionCount;

                // The interruption that stopped the run is now being handled. Clear the request
                // before reverting so that a new interruption is required to stop the reversion.
                _interruptionRequested = false;
                _currentAction = null;
                _status = WorkflowRunnerStatus.Reverting;
            }
        }

        if (executedActionCount == 0)
        {
            CompleteInterruption();
            return;
        }

        RaiseStatusChanged(WorkflowRunnerStatus.Reverting);

        // Start at the action that was running when the interruption arrived.
        await RevertActionsAsync(executedActionCount - 1);
    }

    private async Task StartReversionAsync(int startIndex)
    {
        lock (_statusLock)
        {
            // An interruption may have arrived between the status check and this transition.
            if (_status is not WorkflowRunnerStatus.WaitingForReversion || _interruptionRequested)
            {
                return;
            }

            _interruptionRequested = false;
            _status = WorkflowRunnerStatus.Reverting;
        }

        RaiseStatusChanged(WorkflowRunnerStatus.Reverting);
        await RevertActionsAsync(startIndex);
    }

    private async Task RevertActionsAsync(int startIndex)
    {
        try
        {
            for (var i = startIndex; i >= 0; i--)
            {
                if (IsInterruptionRequested) break;

                ActionBase action;
                lock (_statusLock)
                {
                    action = _actions[i];
                    if (_skippedActions.Contains(action))
                    {
                        continue;
                    }

                    _currentAction = action;
                }

                try
                {
                    await action.RevertAsync();
                }
                catch (OperationCanceledException) when (IsInterruptionRequested)
                {
                    // Interruption is an expected transition and is handled below.
                }
                catch (Exception) when (
                    _exceptionHandleMode is WorkflowRunnerExceptionHandleMode.Skip)
                {
                    // Skip mode: ignore the failed reversion and continue with the remaining actions.
                }
                catch (Exception) when (
                    _exceptionHandleMode is WorkflowRunnerExceptionHandleMode.Interrupt)
                {
                    // Interrupt mode: cancel the remaining reversions and reset the action state machine.
                    RequestInterruption();
                }
                finally
                {
                    lock (_statusLock)
                    {
                        _currentAction = null;
                    }
                }
            }
        }
        finally
        {
            try
            {
                // Reversion has finished or was interrupted; make sure every action is idle again.
                ResetAllActions();
            }
            finally
            {
                CompleteInterruption();
            }
        }
    }

    private void ResetToIdle()
    {
        try
        {
            ResetAllActions();
        }
        finally
        {
            CompleteInterruption();
        }
    }

    private void CompleteInterruption()
    {
        lock (_statusLock)
        {
            _interruptionRequested = false;
            _currentAction = null;
            _skippedActions.Clear();
            _status = WorkflowRunnerStatus.WaitingForTriggering;
        }

        RaiseStatusChanged(WorkflowRunnerStatus.WaitingForTriggering);
    }

    private void ResetAllActions()
    {
        foreach (var action in _actions)
        {
            action.ForceReset();
        }
    }

    private void RequestInterruption()
    {
        var statusChanged = false;

        lock (_statusLock)
        {
            _interruptionRequested = true;

            if (_status is not WorkflowRunnerStatus.Interrupting)
            {
                _status = WorkflowRunnerStatus.Interrupting;
                statusChanged = true;
            }
        }

        if (statusChanged)
        {
            RaiseStatusChanged(WorkflowRunnerStatus.Interrupting);
        }
    }

    private bool IsInterruptionRequested
    {
        get
        {
            lock (_statusLock)
            {
                return _interruptionRequested;
            }
        }
    }

    private static void SetProfile(TriggerBase trigger, TriggerProfileBase profile)
    {
        var profileProperty = trigger.GetType().GetProperty(
            nameof(TriggerBase<TriggerProfileBase>.Profile),
            BindingFlags.Instance | BindingFlags.Public)
            ?? throw new InvalidOperationException(
                $"Trigger type '{trigger.GetType().FullName}' has no profile property.");

        profileProperty.SetValue(trigger, profile);
    }

    private static void SetProfile(ActionBase action, ActionProfileBase profile)
    {
        var profileProperty = action.GetType().GetProperty(
            nameof(ActionBase<ActionProfileBase>.Profile),
            BindingFlags.Instance | BindingFlags.Public)
            ?? throw new InvalidOperationException(
                $"Action type '{action.GetType().FullName}' has no profile property.");

        profileProperty.SetValue(action, profile);
    }

    private void OnStatusChanged(WorkflowRunnerStatus status)
    {
        lock (_statusLock)
        {
            _status = status;
        }

        RaiseStatusChanged(status);
    }

    private void RaiseStatusChanged(WorkflowRunnerStatus status)
    {
        var handlers = StatusChanged;
        if (handlers is null) return;

        var eventArgs = new WorkflowRunnerStatusChangedEventArgs(status);

        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((EventHandler<WorkflowRunnerStatusChangedEventArgs>)handler)(this, eventArgs);
            }
            catch (Exception exception)
            {
                // A faulty subscriber must not break the workflow state machine.
                Debug.WriteLine(exception);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_statusLock)
        {
            if (_disposed) return;
            _disposed = true;
        }

        foreach (var trigger in _triggers)
        {
            trigger.Triggered -= OnTriggered;
        }

        if (Status is WorkflowRunnerStatus.VerifyingRuleset
            or WorkflowRunnerStatus.Running
            or WorkflowRunnerStatus.Interrupting
            or WorkflowRunnerStatus.Reverting
            or WorkflowRunnerStatus.WaitingForReversion)
        {
            Interrupt();
        }

        var initializeLockTaken = false;
        try
        {
            await _initializeLock.WaitAsync();
            initializeLockTaken = true;
        }
        catch (ObjectDisposedException)
        {
            // Already disposed.
        }

        var triggerLockTaken = false;
        try
        {
            await _triggerLock.WaitAsync();
            triggerLockTaken = true;
        }
        catch (ObjectDisposedException)
        {
            // Already disposed.
        }

        try
        {
            OnStatusChanged(WorkflowRunnerStatus.Destroying);

            foreach (var trigger in _triggers.ToArray())
            {
                trigger.Triggered -= OnTriggered;

                try
                {
                    await trigger.DisposeAsync();
                }
                catch (Exception exception)
                {
                    Debug.WriteLine(exception);
                }
            }

            _triggers.Clear();
            _actions.Clear();
            Ruleset = null!;
            Initialized = false;
        }
        finally
        {
            if (triggerLockTaken)
            {
                _triggerLock.Release();
            }

            if (initializeLockTaken)
            {
                _initializeLock.Release();
            }
        }

        _initializeLock.Dispose();
        _triggerLock.Dispose();
    }
}
