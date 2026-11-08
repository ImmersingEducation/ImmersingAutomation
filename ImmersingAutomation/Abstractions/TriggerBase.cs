using ImmersingAutomation.Enums;

namespace ImmersingAutomation.Abstractions;

public abstract class TriggerBase : IAsyncDisposable
{
    protected object _profile { get; set; }

    private readonly object _statusLock = new();

    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);

    private TriggerStatus _status = TriggerStatus.Created;

    public TriggerStatus Status
    {
        get
        {
            lock (_statusLock)
            {
                return _status;
            }
        }
    }

    public event EventHandler? Triggered;

    /// <summary>
    /// Raises <see cref="Triggered"/> only while the trigger is ready.
    /// Returns <see langword="false"/> when the trigger is not in the ready state.
    /// </summary>
    protected bool Trigger()
    {
        lock (_statusLock)
        {
            if (_status != TriggerStatus.Ready)
            {
                return false;
            }
        }

        Triggered?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public async Task InitializeAsync()
    {
        await _lifecycleLock.WaitAsync();

        try
        {
            lock (_statusLock)
            {
                switch (_status)
                {
                    case TriggerStatus.Disposed:
                        throw new ObjectDisposedException(GetType().FullName);
                    case TriggerStatus.Ready:
                        return;
                    case TriggerStatus.Created:
                    case TriggerStatus.Released:
                    case TriggerStatus.Faulted:
                        _status = TriggerStatus.Initializing;
                        break;
                    default:
                        throw new InvalidOperationException(
                            $"Cannot initialize a trigger while its status is '{_status}'.");
                }
            }

            try
            {
                await _InitializeAsync();
            }
            catch
            {
                SetStatus(TriggerStatus.Faulted);
                throw;
            }

            SetStatus(TriggerStatus.Ready);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task ReleaseAsync()
    {
        await _lifecycleLock.WaitAsync();

        try
        {
            await ReleaseCoreAsync();
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycleLock.WaitAsync();

        try
        {
            lock (_statusLock)
            {
                if (_status == TriggerStatus.Disposed)
                {
                    return;
                }
            }

            await ReleaseCoreAsync();
        }
        finally
        {
            SetStatus(TriggerStatus.Disposed);
            _lifecycleLock.Release();
        }
    }

    protected abstract Task _InitializeAsync();

    protected abstract Task _ReleaseAsync();

    private async Task ReleaseCoreAsync()
    {
        lock (_statusLock)
        {
            switch (_status)
            {
                case TriggerStatus.Created:
                case TriggerStatus.Released:
                case TriggerStatus.Disposed:
                    return;
                case TriggerStatus.Ready:
                case TriggerStatus.Faulted:
                    _status = TriggerStatus.Releasing;
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Cannot release a trigger while its status is '{_status}'.");
            }
        }

        try
        {
            await _ReleaseAsync();
        }
        catch
        {
            SetStatus(TriggerStatus.Faulted);
            throw;
        }

        SetStatus(TriggerStatus.Released);
    }

    private void SetStatus(TriggerStatus status)
    {
        lock (_statusLock)
        {
            _status = status;
        }
    }
}

public abstract class TriggerBase<T> : TriggerBase where T : TriggerProfileBase
{
    public T Profile
    {
        get => (_profile as T)!;
        init => _profile = value;
    }
}
