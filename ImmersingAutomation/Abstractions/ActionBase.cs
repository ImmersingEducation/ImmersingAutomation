using ImmersingAutomation.Enums;

namespace ImmersingAutomation.Abstractions;

/// <summary>
/// 可运行、并可选回滚的动作的基类。
/// </summary>
/// <remarks>
/// 基类负责状态机和中断处理；派生动作只需实现 <see cref="_RunAsync"/> 和
/// <see cref="_RevertAsync"/>。
/// </remarks>
public abstract class ActionBase
{
    /// <summary>
    /// 获取或设置由泛型派生类提供的配置对象。
    /// </summary>
    protected object _profile = null!;

    private readonly object _statusLock = new();

    private ActionStatus _status = ActionStatus.WaitingForRunning;

    private CancellationTokenSource _interruptionSource = new();

    private int _operationVersion;

    /// <summary>
    /// 获取动作的当前状态。
    /// </summary>
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

    /// <summary>
    /// 获取在本次运行或回滚被中断时取消的标记。
    /// </summary>
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
    /// 请求正在运行或回滚的动作停止。
    /// 动作处于空闲状态时返回 <see langword="false"/>。
    /// </summary>
    public bool Interrupt()
    {
        CancellationTokenSource interruptionSource;

        lock (_statusLock)
        {
            if (_status is not (ActionStatus.Running or ActionStatus.Reverting))
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
            // 动作已经结束并继续执行，因此没有需要中断的内容。
        }

        return true;
    }

    /// <summary>
    /// 当前操作已被中断时引发 <see cref="OperationCanceledException"/>。
    /// </summary>
    protected void ThrowIfInterrupted()
    {
        InterruptionToken.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// 运行动作。
    /// 当 <paramref name="isReversionNeeded"/> 为 <see langword="true"/> 时，动作运行后等待
    /// <see cref="RevertAsync"/>；否则直接返回空闲状态。
    /// </summary>
    /// <param name="isReversionNeeded">动作运行后是否需要回滚。</param>
    public async Task RunAsync(bool isReversionNeeded)
    {
        CancellationTokenSource interruptionSource;
        CancellationToken interruptionToken;
        int operationVersion;

        lock (_statusLock)
        {
            EnsureStatus(ActionStatus.WaitingForRunning, nameof(RunAsync));

            ResetInterruptionSource();
            interruptionSource = _interruptionSource;
            _status = ActionStatus.Running;
            operationVersion = ++_operationVersion;
            interruptionToken = interruptionSource.Token;
        }

        try
        {
            interruptionToken.ThrowIfCancellationRequested();
            await _RunAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (interruptionToken.IsCancellationRequested)
        {
            // 中断是预期的状态转换，将在下方统一处理。
        }
        finally
        {
            var disposeInterruptionSource = false;

            lock (_statusLock)
            {
                // 强制重置会取代本次操作，因此不能恢复旧状态。
                if (_operationVersion == operationVersion)
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
                else
                {
                    // ForceReset 替换了取消源；由本次操作负责释放不再被引用的源。
                    disposeInterruptionSource = !ReferenceEquals(_interruptionSource, interruptionSource);
                }
            }

            if (disposeInterruptionSource)
            {
                interruptionSource.Dispose();
            }
        }
    }

    /// <summary>
    /// 回滚处于等待回滚状态的动作，并将其恢复到空闲状态。
    /// </summary>
    public async Task RevertAsync()
    {
        CancellationTokenSource interruptionSource;
        CancellationToken interruptionToken;
        int operationVersion;

        lock (_statusLock)
        {
            EnsureStatus(ActionStatus.WaitingForReversion, nameof(RevertAsync));

            ResetInterruptionSource();
            interruptionSource = _interruptionSource;
            _status = ActionStatus.Reverting;
            operationVersion = ++_operationVersion;
            interruptionToken = interruptionSource.Token;
        }

        try
        {
            interruptionToken.ThrowIfCancellationRequested();
            await _RevertAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (interruptionToken.IsCancellationRequested)
        {
            // 中断是预期的状态转换，将在下方统一处理。
        }
        finally
        {
            var disposeInterruptionSource = false;

            lock (_statusLock)
            {
                // 强制重置会取代本次操作，因此不能恢复旧状态。
                if (_operationVersion == operationVersion)
                {
                    ResetInterruptionSource();
                    _status = ActionStatus.WaitingForRunning;
                }
                else
                {
                    // ForceReset 替换了取消源；由本次操作负责释放不再被引用的源。
                    disposeInterruptionSource = !ReferenceEquals(_interruptionSource, interruptionSource);
                }
            }

            if (disposeInterruptionSource)
            {
                interruptionSource.Dispose();
            }
        }
    }

    /// <summary>
    /// 跳过回滚并将动作恢复到空闲状态。
    /// 仅在工作流不需要回滚时使用。
    /// </summary>
    public void Reset()
    {
        lock (_statusLock)
        {
            EnsureStatus(ActionStatus.WaitingForReversion, nameof(Reset));

            _operationVersion++;
            ResetInterruptionSource();
            _status = ActionStatus.WaitingForRunning;
        }
    }

    /// <summary>
    /// 无论当前状态如何，都将动作重置为空闲状态。
    /// 如果操作仍在运行，会取消该操作并忽略其最终状态转换。
    /// </summary>
    public void ForceReset()
    {
        CancellationTokenSource interruptionSource;
        bool disposeInterruptionSource;

        lock (_statusLock)
        {
            if (_status is ActionStatus.WaitingForRunning)
            {
                return;
            }

            // 运行或回滚中的操作可能仍持有旧取消源的标记。
            // 由该操作的 finally 负责释放取消源，避免在标记仍使用时将其销毁。
            disposeInterruptionSource = _status is not (
                ActionStatus.Running
                or ActionStatus.Reverting
                or ActionStatus.Interrupting);

            _operationVersion++;
            interruptionSource = _interruptionSource;
            _interruptionSource = new CancellationTokenSource();
            _status = ActionStatus.WaitingForRunning;
        }

        try
        {
            interruptionSource.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 操作已经结束并释放了其取消源。
        }

        if (disposeInterruptionSource)
        {
            interruptionSource.Dispose();
        }
    }

    /// <summary>
    /// 运行动作。实现应响应 <see cref="InterruptionToken"/>。
    /// </summary>
    protected abstract Task _RunAsync();

    /// <summary>
    /// 回滚动作。实现应响应 <see cref="InterruptionToken"/>。
    /// </summary>
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

/// <summary>
/// 使用强类型配置的动作的基类。
/// </summary>
/// <typeparam name="T">动作的配置类型。</typeparam>
public abstract class ActionBase<T> : ActionBase where T : ActionProfileBase
{
    /// <summary>
    /// 获取或设置动作的配置。
    /// </summary>
    public T Profile
    {
        get => (_profile as T)!;
        init => _profile = value;
    }
}
