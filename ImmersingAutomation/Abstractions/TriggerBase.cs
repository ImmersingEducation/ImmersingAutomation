using ImmersingAutomation.Enums;

namespace ImmersingAutomation.Abstractions;

/// <summary>
/// 可初始化、轮询并释放的触发器的基类。
/// </summary>
/// <remarks>
/// 基类负责状态机并串行化生命周期调用；派生触发器需实现
/// <see cref="_InitializeAsync"/>、<see cref="_ReleaseAsync"/> 和 <see cref="Validate"/>。
/// </remarks>
public abstract class TriggerBase : IAsyncDisposable
{
    /// <summary>
    /// 获取或设置由泛型派生类提供的配置对象。
    /// </summary>
    protected object _profile { get; set; } = null!;

    private readonly object _statusLock = new();

    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);

    private TriggerStatus _status = TriggerStatus.Created;

    /// <summary>
    /// 获取触发器的当前状态。
    /// </summary>
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

    /// <summary>
    /// 当 <see cref="Trigger"/> 检测到触发条件满足时发生。
    /// </summary>
    public event EventHandler? Triggered;

    /// <summary>
    /// 确定触发器当前是否可以触发。
    /// 默认实现返回 <see langword="false"/>；派生触发器会重写此方法以检查自身的触发条件。
    /// 就绪状态由 <see cref="Trigger"/> 负责校验。
    /// </summary>
    public virtual bool Validate()
    {
        return false;
    }

    /// <summary>
    /// 仅当触发器已就绪且条件满足时引发 <see cref="Triggered"/>。
    /// 触发器不处于就绪状态时返回 <see langword="false"/>。
    /// </summary>
    public bool Trigger()
    {
        lock (_statusLock)
        {
            if (_status is not TriggerStatus.Ready)
            {
                return false;
            }
        }

        // Validate 可能读取外部状态，因此不要持有状态锁执行。
        if (!Validate())
        {
            return false;
        }

        Triggered?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// 初始化触发器并将其切换到 <see cref="TriggerStatus.Ready"/>。
    /// </summary>
    /// <exception cref="ObjectDisposedException">触发器已销毁。</exception>
    /// <exception cref="InvalidOperationException">无法从当前状态初始化触发器。</exception>
    public async Task InitializeAsync()
    {
        await _lifecycleLock.WaitAsync().ConfigureAwait(false);

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
                await _InitializeAsync().ConfigureAwait(false);
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

    /// <summary>
    /// 释放触发器，使其可以在之后再次初始化。
    /// </summary>
    public async Task ReleaseAsync()
    {
        await _lifecycleLock.WaitAsync().ConfigureAwait(false);

        try
        {
            await ReleaseCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    /// <summary>
    /// 释放并销毁触发器。之后的生命周期调用将被拒绝。
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await _lifecycleLock.WaitAsync().ConfigureAwait(false);

        try
        {
            lock (_statusLock)
            {
                if (_status == TriggerStatus.Disposed)
                {
                    return;
                }
            }

            await ReleaseCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            SetStatus(TriggerStatus.Disposed);
            _lifecycleLock.Release();
        }
    }

    /// <summary>
    /// 初始化触发器自身的资源。
    /// </summary>
    protected abstract Task _InitializeAsync();

    /// <summary>
    /// 释放触发器自身的资源。
    /// </summary>
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
            await _ReleaseAsync().ConfigureAwait(false);
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

/// <summary>
/// 使用强类型配置的触发器的基类。
/// </summary>
/// <typeparam name="T">触发器的配置类型。</typeparam>
public abstract class TriggerBase<T> : TriggerBase where T : TriggerProfileBase
{
    /// <summary>
    /// 获取或设置触发器的配置。
    /// </summary>
    public T Profile
    {
        get => (_profile as T)!;
        init => _profile = value;
    }
}
