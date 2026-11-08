using System.Diagnostics;
using System.Reflection;
using ImmersingAutomation.Abstractions;
using ImmersingAutomation.Attributes;
using ImmersingAutomation.Enums;
using ImmersingAutomation.Events;
using Microsoft.Extensions.DependencyInjection;

namespace ImmersingAutomation.Models;

/// <summary>
/// 执行单个 <see cref="Workflow"/>：根据工作流配置创建触发器、规则集和动作，
/// 并驱动它们完成状态转换。
/// </summary>
public class WorkflowRunner : IAsyncDisposable
{
    /// <summary>
    /// 获取运行器是否已成功初始化。
    /// </summary>
    public bool Initialized { get; private set; } = false;

    /// <summary>
    /// 获取此运行器实例的唯一标识符。
    /// </summary>
    public Guid Guid { get; } = Guid.NewGuid();

    /// <summary>
    /// 获取此运行器执行的工作流。
    /// </summary>
    public Workflow Workflow { get; }

    /// <summary>
    /// 获取运行器的当前状态。
    /// </summary>
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

    /// <summary>
    /// 获取根据 <see cref="Workflow.TriggerProfiles"/> 创建的触发器。
    /// </summary>
    public IReadOnlyList<TriggerBase> Triggers { get; }

    /// <summary>
    /// 获取根据 <see cref="Workflow.RulesetProfile"/> 创建的规则集。
    /// </summary>
    public Ruleset Ruleset { get; private set; } = null!;

    /// <summary>
    /// 获取根据 <see cref="Workflow.ActionProfiles"/> 创建的动作。
    /// </summary>
    public IReadOnlyList<ActionBase> Actions { get; }

    /// <summary>
    /// 当运行器状态变化时发生。
    /// </summary>
    public event EventHandler<WorkflowRunnerStatusChangedEventArgs>? StatusChanged;

    /// <summary>
    /// 当后台工作流执行失败时发生。
    /// </summary>
    /// <remarks>
    /// 动作失败、规则集验证失败以及异步触发处理中的未处理异常都会引发此事件。
    /// </remarks>
    public event EventHandler<AutomationErrorEventArgs>? ErrorOccurred;

    private readonly SemaphoreSlim _triggerLock = new(1, 1);

    private readonly SemaphoreSlim _initializeLock = new(1, 1);

    private readonly object _statusLock = new();

    private readonly IServiceProvider? _serviceProvider;

    private CancellationTokenSource _verificationSource = new();

    private readonly HashSet<ActionBase> _skippedActions = [];

    private WorkflowRunnerStatus _status;

    private ActionBase? _currentAction;

    private int _executedActionCount;

    private bool _interruptionRequested;

    private bool _isReversionEnabled;

    private WorkflowRunnerExceptionHandleMode _exceptionHandleMode = WorkflowRunnerExceptionHandleMode.Skip;

    private bool _disposed;

    /// <summary>
    /// 初始化 <see cref="WorkflowRunner"/> 类的新实例。
    /// </summary>
    /// <param name="workflow">要执行的工作流。</param>
    /// <param name="serviceProvider">
    /// 用于创建触发器和动作的服务提供程序；为 <see langword="null"/> 时使用无参构造函数。
    /// </param>
    public WorkflowRunner(Workflow workflow, IServiceProvider? serviceProvider = null)
    {
        Workflow = workflow ?? throw new ArgumentNullException(nameof(workflow));
        _serviceProvider = serviceProvider;
        Triggers = _triggers.AsReadOnly();
        Actions = _actions.AsReadOnly();
    }

    /// <summary>
    /// 同步初始化运行器。在异步路径中优先使用 <see cref="InitializeAsync"/>。
    /// </summary>
    public void Initialize()
    {
        Task.Run(InitializeAsync).GetAwaiter().GetResult();
    }

    /// <summary>
    /// 根据工作流配置创建触发器、规则集和动作，并初始化触发器。
    /// 运行器初始化完成后，重复调用将被忽略。
    /// </summary>
    /// <exception cref="ObjectDisposedException">运行器已销毁。</exception>
    public async Task InitializeAsync()
    {
        try
        {
            await _initializeLock.WaitAsync().ConfigureAwait(false);
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

                    var trigger = CreateInstance(profileAttr.TriggerType) as TriggerBase
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

                    if (Workflow.IsReversionEnabled)
                    {
                        var actionAttribute = profileAttr.ActionType
                            .GetCustomAttribute<ActionAttribute>(inherit: false);

                        if (actionAttribute is not null && !actionAttribute.IsReversionSupported)
                        {
                            throw new InvalidOperationException(
                                $"Action type '{profileAttr.ActionType.FullName}' does not support reversion, " +
                                $"but workflow '{Workflow.Name}' has reversion enabled.");
                        }
                    }

                    var action = CreateInstance(profileAttr.ActionType) as ActionBase
                        ?? throw new InvalidOperationException(
                            $"Failed to create action type '{profileAttr.ActionType.FullName}'.");

                    SetProfile(action, actionProfile);
                    _actions.Add(action);
                }

                OnStatusChanged(WorkflowRunnerStatus.ExternalInitializing);

                foreach (var trigger in _triggers)
                {
                    await trigger.InitializeAsync().ConfigureAwait(false);
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
                        await trigger.DisposeAsync().ConfigureAwait(false);
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

    /// <summary>
    /// 处理触发器事件：验证规则集，匹配时执行动作。
    /// 触发器处理是串行化的，因此并发触发不会导致工作流执行两次。
    /// </summary>
    private void OnTriggered(object? sender, EventArgs eventArgs)
    {
        _ = HandleTriggeredAsync();
    }

    private async Task HandleTriggeredAsync()
    {
        if (_disposed || Status is not WorkflowRunnerStatus.WaitingForTriggering) return;

        // 让触发器轮询循环立即返回，避免同步完成的动作占用轮询线程。
        await Task.Yield();

        // 串行化触发器处理，避免并发触发同时执行规则集和动作。
        try
        {
            await _triggerLock.WaitAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            // 获取锁后再次检查：等待期间状态可能已变化。
            if (_disposed || Status is not WorkflowRunnerStatus.WaitingForTriggering) return;

            lock (_statusLock)
            {
                // 快照工作流设置，避免之后的修改影响本次运行。
                _isReversionEnabled = Workflow.IsReversionEnabled;
                _exceptionHandleMode = Workflow.ExceptionHandleMode;
            }

            CancellationToken verificationToken;
            lock (_statusLock)
            {
                if (_disposed)
                {
                    return;
                }

                ResetVerificationSource();
                verificationToken = _verificationSource.Token;
            }

            OnStatusChanged(WorkflowRunnerStatus.VerifyingRuleset);

            bool rulesetResult;
            try
            {
                rulesetResult = await Ruleset.ValidateAsync(verificationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (verificationToken.IsCancellationRequested)
            {
                CompleteInterruption();
                return;
            }

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
                    await action.RunAsync(_isReversionEnabled).ConfigureAwait(false);
                }
                catch (Exception exception) when (
                    _exceptionHandleMode is WorkflowRunnerExceptionHandleMode.Skip)
                {
                    RaiseError(AutomationErrorSource.Action, "Run action", exception, action);

                    // 跳过模式：记录失败的动作，以便回滚时跳过它，并继续执行剩余动作。
                    if (_isReversionEnabled)
                    {
                        lock (_statusLock)
                        {
                            _skippedActions.Add(action);
                        }

                        action.ForceReset();
                    }
                }
                catch (Exception exception) when (
                    _exceptionHandleMode is WorkflowRunnerExceptionHandleMode.Interrupt)
                {
                    RaiseError(AutomationErrorSource.Action, "Run action", exception, action);

                    // 中断模式：停止剩余动作，并从失败的动作开始恢复。
                    RequestInterruption();
                }
                finally
                {
                    lock (_statusLock)
                    {
                        _currentAction = null;
                    }
                }

                // 当前动作要么已完成，要么已被中断并等待回滚，因此属于可能需要回滚的动作。
                lock (_statusLock)
                {
                    _executedActionCount = i + 1;
                }
            }

            if (IsInterruptionRequested)
            {
                await RevertAfterInterruptionAsync().ConfigureAwait(false);
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
                await RevertAfterInterruptionAsync().ConfigureAwait(false);
            }
            else
            {
                RaiseStatusChanged(WorkflowRunnerStatus.WaitingForReversion);
            }
        }
        catch (Exception exception)
        {
            RaiseError(AutomationErrorSource.Workflow, "Execute workflow", exception);
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
                // 触发器处理器结束时运行器已被销毁。
            }
        }
    }

    /// <summary>
    /// 请求中断当前运行或回滚。
    /// 运行期间请求中断时，回滚从正在运行的动作开始；回滚期间请求中断时，
    /// 取消所有剩余回滚并重置动作状态机。
    /// 当前无法中断工作流运行器时返回 <see langword="false"/>。
    /// </summary>
    public bool Interrupt()
    {
        ActionBase? currentAction;
        CancellationTokenSource? verificationSource = null;
        bool resetImmediately;

        lock (_statusLock)
        {
            switch (_status)
            {
                case WorkflowRunnerStatus.VerifyingRuleset:
                    _interruptionRequested = true;
                    _status = WorkflowRunnerStatus.Interrupting;
                    currentAction = null;
                    verificationSource = _verificationSource;
                    resetImmediately = false;
                    break;

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

        if (verificationSource is not null)
        {
            try
            {
                verificationSource.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // 规则集验证已经结束并释放了取消源。
            }
        }

        if (resetImmediately)
        {
            // 当前没有回滚循环在运行，因此直接在此取消待处理的回滚，
            // 并在不调用动作回滚的情况下将所有动作恢复到空闲状态。
            ResetAllActions();
            CompleteInterruption();
            return true;
        }

        currentAction?.Interrupt();
        return true;
    }

    /// <summary>
    /// 当运行器等待回滚时，开始回滚动作。
    /// </summary>
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
            await _triggerLock.WaitAsync().ConfigureAwait(false);
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

            await StartReversionAsync(executedActionCount - 1).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            RaiseError(AutomationErrorSource.Workflow, "Revert workflow", exception);
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
                // 回滚处理器结束时运行器已被销毁。
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

                // 停止本次运行的中断现在正在被处理。回滚前清除请求，
                // 以便需要新的中断才能停止回滚。
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

        // 从中断到达时正在运行的动作开始回滚。
        await RevertActionsAsync(executedActionCount - 1).ConfigureAwait(false);
    }

    private async Task StartReversionAsync(int startIndex)
    {
        lock (_statusLock)
        {
            // 中断可能在状态检查和本次状态转换之间到达。
            if (_status is not WorkflowRunnerStatus.WaitingForReversion || _interruptionRequested)
            {
                return;
            }

            _interruptionRequested = false;
            _status = WorkflowRunnerStatus.Reverting;
        }

        RaiseStatusChanged(WorkflowRunnerStatus.Reverting);
        await RevertActionsAsync(startIndex).ConfigureAwait(false);
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
                    await action.RevertAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (IsInterruptionRequested)
                {
                    // 中断是预期的状态转换，将在下方统一处理。
                }
                catch (Exception exception) when (
                    _exceptionHandleMode is WorkflowRunnerExceptionHandleMode.Skip)
                {
                    RaiseError(AutomationErrorSource.ActionReversion, "Revert action", exception, action);

                    // 跳过模式：忽略失败的回滚，继续执行剩余动作。
                }
                catch (Exception exception) when (
                    _exceptionHandleMode is WorkflowRunnerExceptionHandleMode.Interrupt)
                {
                    RaiseError(AutomationErrorSource.ActionReversion, "Revert action", exception, action);

                    // 中断模式：取消剩余回滚并重置动作状态机。
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
                // 回滚已完成或已被中断；确保所有动作都恢复到空闲状态。
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

    private object CreateInstance(Type type)
    {
        if (_serviceProvider is not null)
        {
            // 使用 DI 解析构造函数依赖，但始终创建新实例：
            // 触发器和动作都是有状态的，不能由多个工作流共享。
            return ActivatorUtilities.CreateInstance(_serviceProvider, type);
        }

        return Activator.CreateInstance(type)
               ?? throw new InvalidOperationException(
                   $"Failed to create instance of type '{type.FullName}'.");
    }

    private void ResetVerificationSource()
    {
        var previousVerificationSource = _verificationSource;

        _verificationSource = new CancellationTokenSource();
        previousVerificationSource.Dispose();
    }

    private void RaiseError(
        AutomationErrorSource source,
        string operation,
        Exception exception,
        ActionBase? action = null)
    {
        Debug.WriteLine($"[WorkflowRunner] {operation} failed: {exception}");

        var handlers = ErrorOccurred;
        if (handlers is null)
        {
            return;
        }

        var eventArgs = new AutomationErrorEventArgs(source, operation, exception, this, action);

        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((EventHandler<AutomationErrorEventArgs>)handler)(this, eventArgs);
            }
            catch (Exception handlerException)
            {
                // 错误订阅者自身出错时不能破坏工作流状态机。
                Debug.WriteLine(handlerException);
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
                // 单个订阅者出错不应破坏工作流状态机。
                RaiseError(
                    AutomationErrorSource.StatusChanged,
                    "Notify status changed",
                    exception);
            }
        }
    }

    /// <summary>
    /// 中断所有正在进行的运行或回滚，并释放触发器和动作。
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        lock (_statusLock)
        {
            if (_disposed) return;
            _disposed = true;
        }

        try
        {
            _verificationSource.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 规则集验证已经结束并释放了取消源。
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
            await _initializeLock.WaitAsync().ConfigureAwait(false);
            initializeLockTaken = true;
        }
        catch (ObjectDisposedException)
        {
            // 已经销毁。
        }

        var triggerLockTaken = false;
        try
        {
            await _triggerLock.WaitAsync().ConfigureAwait(false);
            triggerLockTaken = true;
        }
        catch (ObjectDisposedException)
        {
            // 已经销毁。
        }

        try
        {
            OnStatusChanged(WorkflowRunnerStatus.Destroying);

            foreach (var trigger in _triggers.ToArray())
            {
                trigger.Triggered -= OnTriggered;

                try
                {
                    await trigger.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    RaiseError(
                        AutomationErrorSource.Disposal,
                        $"Dispose trigger '{trigger.GetType().FullName}'",
                        exception);
                }
            }

            _triggers.Clear();
            _actions.Clear();
            Ruleset = null!;
            Initialized = false;
        }
        finally
        {
            _verificationSource.Dispose();

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
