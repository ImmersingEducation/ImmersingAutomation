using ImmersingAutomation.Enums;

namespace ImmersingAutomation.Abstractions;

public abstract class ActionBase
{
    protected object _profile;

    private readonly object _statusLock = new();

    private ActionStatus _status = ActionStatus.WaitingForRunning;

    private CancellationTokenSource _interruptionSource = new();

    public ActionStatus Status
    {
        get
        {
            lock (_statusLock)
            {
                return _status;
            }
        }
    }

    protected CancellationToken InterruptionToken
    {
        get
        {
            lock (_statusLock)
            {
                return _interruptionSource.Token;
            }
        }
    }

    /// <summary>
    /// Requests the running action to stop.
    /// Returns <see langword="false"/> when the action is not running.
    /// </summary>
    public bool Interrupt()
    {
        CancellationTokenSource interruptionSource;

        lock (_statusLock)
        {
            if (_status != ActionStatus.Running)
            {
                return false;
            }

            _status = ActionStatus.Interrupting;
            interruptionSource = _interruptionSource;
        }

        try
        {
            interruptionSource.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The action already finished and moved on, so there is nothing left to interrupt.
        }

        return true;
    }

    protected void ThrowIfInterrupted()
    {
        InterruptionToken.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// Runs the action.
    /// When <paramref name="isReversionNeeded"/> is <see langword="true"/>, the action waits for
    /// <see cref="RevertAsync"/> after running; otherwise it returns directly to the idle state.
    /// </summary>
    public async Task RunAsync(bool isReversionNeeded)
    {
        CancellationToken interruptionToken;

        lock (_statusLock)
        {
            EnsureStatus(ActionStatus.WaitingForRunning, nameof(RunAsync));

            ResetInterruptionSource();
            _status = ActionStatus.Running;
            interruptionToken = _interruptionSource.Token;
        }

        try
        {
            interruptionToken.ThrowIfCancellationRequested();
            await _RunAsync();
        }
        catch (OperationCanceledException) when (interruptionToken.IsCancellationRequested)
        {
            // Interruption is an expected transition and is handled below.
        }
        finally
        {
            lock (_statusLock)
            {
                if (isReversionNeeded)
                {
                    _status = ActionStatus.WaitingForReversion;
                }
                else
                {
                    ResetInterruptionSource();
                    _status = ActionStatus.WaitingForRunning;
                }
            }
        }
    }

    public async Task RevertAsync()
    {
        lock (_statusLock)
        {
            EnsureStatus(ActionStatus.WaitingForReversion, nameof(RevertAsync));

            ResetInterruptionSource();
            _status = ActionStatus.Reverting;
        }

        try
        {
            await _RevertAsync();
        }
        finally
        {
            lock (_statusLock)
            {
                _status = ActionStatus.WaitingForRunning;
            }
        }
    }

    /// <summary>
    /// Skips reversion and returns the action to the idle state.
    /// Use this only when the workflow does not require reversion.
    /// </summary>
    public void Reset()
    {
        lock (_statusLock)
        {
            EnsureStatus(ActionStatus.WaitingForReversion, nameof(Reset));

            ResetInterruptionSource();
            _status = ActionStatus.WaitingForRunning;
        }
    }

    protected abstract Task _RunAsync();

    protected abstract Task _RevertAsync();

    private void EnsureStatus(ActionStatus expectedStatus, string operation)
    {
        if (_status != expectedStatus)
        {
            throw new InvalidOperationException(
                $"Cannot execute '{operation}' while the action status is '{_status}'. " +
                $"Expected '{expectedStatus}'.");
        }
    }

    private void ResetInterruptionSource()
    {
        var previousInterruptionSource = _interruptionSource;

        _interruptionSource = new CancellationTokenSource();
        previousInterruptionSource.Dispose();
    }
}

public abstract class ActionBase<T> : ActionBase where T : ActionProfileBase
{
    public T Profile
    {
        get => (_profile as T)!;
        init => _profile = value;
    }
}
