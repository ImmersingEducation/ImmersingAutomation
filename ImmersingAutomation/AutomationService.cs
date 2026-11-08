using System.Collections.Concurrent;
using System.Diagnostics;
using ImmersingAutomation.Abstractions;
using ImmersingAutomation.Enums;
using ImmersingAutomation.Events;
using ImmersingAutomation.Models;
using Microsoft.Extensions.DependencyInjection;

namespace ImmersingAutomation;

/// <summary>
/// 承载工作流并驱动其触发器与回滚。
/// </summary>
/// <remarks>
/// 服务会加载已存储的工作流，为每个启用的工作流创建 <see cref="WorkflowRunner"/>，
/// 轮询已注册的触发器，并在启用回滚时轮询等待回滚的运行器的规则集。
/// </remarks>
public class AutomationService : IAsyncDisposable
{
    private readonly ConcurrentDictionary<TriggerBase, byte> _activeTriggers = new();
    private readonly ConcurrentDictionary<WorkflowRunner, byte> _reversionRunners = new();
    private readonly ConcurrentDictionary<Task, byte> _reversionTasks = new();
    private readonly ConcurrentDictionary<WorkflowRunner, Task> _runnerInitializations = new();
    private readonly SemaphoreSlim _runnerLock = new(1, 1);
    private readonly CancellationTokenSource _triggerLoopSource = new();
    private readonly CancellationTokenSource _reversionLoopSource = new();
    private readonly object _initializeLock = new();
    private readonly object _providerLock = new();

    private IServiceCollection _serviceCollection;
    private IServiceProvider? _serviceProvider;
    private bool _ownsServiceProvider;
    private IAutomationStorageService _storageService;
    private List<Workflow> _workflows = [];
    private List<WorkflowRunner> _runners = [];
    private Task? _triggerLoopTask;
    private Task? _reversionLoopTask;
    private Task? _initializeTask;
    private int _triggerLoopRunning;
    private int _reversionLoopRunning;
    private int _disposed;

    private TimeSpan _triggerPollInterval = TimeSpan.FromMilliseconds(100);
    private TimeSpan _reversionPollInterval = TimeSpan.FromMilliseconds(200);
    private int _reversionCheckConcurrency = 4;

    /// <summary>
    /// 初始化 <see cref="AutomationService"/> 类的新实例。
    /// </summary>
    /// <param name="storageService">用于加载和持久化工作流的存储服务。</param>
    public AutomationService(IAutomationStorageService storageService)
        : this(storageService, new ServiceCollection())
    {
    }

    /// <summary>
    /// 初始化 <see cref="AutomationService"/> 类的新实例。
    /// </summary>
    /// <param name="storageService">用于加载和持久化工作流的存储服务。</param>
    /// <param name="serviceCollection">用于解析触发器和动作依赖的服务集合。</param>
    public AutomationService(
        IAutomationStorageService storageService,
        IServiceCollection serviceCollection)
    {
        ArgumentNullException.ThrowIfNull(storageService);
        ArgumentNullException.ThrowIfNull(serviceCollection);

        _storageService = storageService;
        _serviceCollection = serviceCollection;
    }

    /// <summary>
    /// 当后台自动化操作失败时发生。
    /// </summary>
    /// <remarks>
    /// 触发器轮询、回滚检查、工作流初始化或后台事件处理中的异常都会引发此事件。
    /// </remarks>
    public event EventHandler<AutomationErrorEventArgs>? ErrorOccurred;

    /// <summary>
    /// 获取服务是否已初始化。
    /// </summary>
    public bool IsInitialized { get; private set; }

    /// <summary>
    /// 获取或设置两次轮询已注册触发器之间的间隔。
    /// </summary>
    public TimeSpan TriggerPollInterval
    {
        get => _triggerPollInterval;
        init => _triggerPollInterval = ValidateInterval(value, nameof(TriggerPollInterval));
    }

    /// <summary>
    /// 获取或设置两次检查等待回滚的规则集之间的间隔。
    /// </summary>
    public TimeSpan ReversionPollInterval
    {
        get => _reversionPollInterval;
        init => _reversionPollInterval = ValidateInterval(value, nameof(ReversionPollInterval));
    }

    /// <summary>
    /// 获取或设置单次回滚轮询中并发验证的规则集最大数量。
    /// </summary>
    public int ReversionCheckConcurrency
    {
        get => _reversionCheckConcurrency;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            _reversionCheckConcurrency = value;
        }
    }

    /// <summary>
    /// 获取或设置用于构建服务提供程序的服务集合；服务初始化后为 <see langword="null"/>。
    /// </summary>
    public IServiceCollection? ServiceCollection
    {
        get => !IsInitialized ? _serviceCollection : null;
        set => _serviceCollection = value ?? new ServiceCollection();
    }

    /// <summary>
    /// 获取或设置用于解析自动化类型的服务提供程序；服务未初始化时为 <see langword="null"/>。
    /// 设置提供程序也会将服务标记为已初始化，但不会加载工作流或启动查询循环。
    /// </summary>
    public IServiceProvider? ServiceProvider
    {
        get => IsInitialized ? _serviceProvider : null;
        set
        {
            if (value is null)
            {
                return;
            }

            lock (_providerLock)
            {
                _serviceProvider = value;
                _ownsServiceProvider = false;
            }

            IsInitialized = true;
        }
    }

    /// <summary>
    /// 加载已存储的工作流，启动已启用工作流的运行器，并启动触发器和回滚查询循环。
    /// 重复调用会等待同一个初始化过程完成。
    /// </summary>
    public async Task InitializeAsync()
    {
        Task initializeTask;

        lock (_initializeLock)
        {
            ThrowIfDisposed();
            initializeTask = _initializeTask ??= InitializeCoreAsync();
        }

        try
        {
            await initializeTask.ConfigureAwait(false);
        }
        catch
        {
            lock (_initializeLock)
            {
                if (ReferenceEquals(_initializeTask, initializeTask))
                {
                    _initializeTask = null;
                }
            }

            throw;
        }
    }

    private async Task InitializeCoreAsync()
    {
        // 先构建提供程序，避免初始化过程中向服务集合继续添加注册项。
        GetOrBuildServiceProvider();

        _workflows = (await _storageService.LoadWorkflowsAsync().ConfigureAwait(false)).ToList();

        foreach (var workflow in _workflows)
        {
            // 初始化失败后可能会重试，因此不要重复订阅。
            workflow.IsEnabledChanged -= OnWorkflowIsEnabledChanged;
            workflow.IsEnabledChanged += OnWorkflowIsEnabledChanged;
        }

        var processedWorkflows = new List<Workflow>();

        try
        {
            foreach (var workflow in _workflows.Where(workflow => workflow.IsEnabled))
            {
                processedWorkflows.Add(workflow);
                await AddWorkflowRunner(workflow).ConfigureAwait(false);
            }

            StartTriggerQueryLoop();
            StartReversionQueryLoop();
            IsInitialized = true;
        }
        catch (Exception exception)
        {
            // 回滚本次初始化中已经创建的工作流，避免重试时复用指向旧对象的运行器。
            foreach (var workflow in processedWorkflows)
            {
                try
                {
                    await RemoveWorkflowRunner(workflow).ConfigureAwait(false);
                }
                catch (Exception rollbackException)
                {
                    RaiseError(
                        AutomationErrorSource.Workflow,
                        $"Roll back workflow '{workflow.Name}'",
                        rollbackException);
                }
            }

            foreach (var workflow in _workflows)
            {
                workflow.IsEnabledChanged -= OnWorkflowIsEnabledChanged;
            }

            _workflows.Clear();
            RaiseError(AutomationErrorSource.Workflow, "Initialize automation service", exception);
            throw;
        }
    }

    private void StartTriggerQueryLoop()
    {
        if (Interlocked.CompareExchange(ref _triggerLoopRunning, 1, 0) != 0)
        {
            return;
        }

        _triggerLoopTask = Task.Run(() => RunTriggerQueryLoopAsync(_triggerLoopSource.Token));
    }

    /// <summary>
    /// 轮询已注册的触发器，直到 <paramref name="cancellationToken"/> 被取消。
    /// 同一时间只能运行一个循环。
    /// </summary>
    public async Task TriggerQueryLoop(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (Interlocked.CompareExchange(ref _triggerLoopRunning, 1, 0) != 0)
        {
            throw new InvalidOperationException("The trigger query loop is already running.");
        }

        // 服务生命周期也会停止由外部调用方启动的循环。
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _triggerLoopSource.Token);

        await RunTriggerQueryLoopAsync(linkedSource.Token).ConfigureAwait(false);
    }

    private async Task RunTriggerQueryLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TriggerPollInterval);

            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                // Keys 返回快照，因此在循环期间注册/注销是安全的。
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
                        // Trigger() 内部会进行验证；先调用 Validate() 会重复评估条件，
                        // 这对消费型触发器来说是错误的。
                        trigger.Trigger();
                    }
                    catch (Exception exception)
                    {
                        // 单个触发器出错不应停止整个轮询循环。
                        RaiseError(
                            AutomationErrorSource.Trigger,
                            $"Poll trigger '{trigger.GetType().FullName}'",
                            exception,
                            trigger: trigger);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 服务关闭时属于预期情况。
        }
        catch (Exception exception)
        {
            RaiseError(AutomationErrorSource.Trigger, "Run trigger query loop", exception);
        }
        finally
        {
            Volatile.Write(ref _triggerLoopRunning, 0);
        }
    }

    /// <summary>
    /// 停止由 <see cref="InitializeAsync"/> 启动的触发器查询循环，并等待其结束。
    /// </summary>
    public async Task StopTriggerQueryLoopAsync()
    {
        await _triggerLoopSource.CancelAsync().ConfigureAwait(false);

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
            // 服务关闭时属于预期情况。
        }
        catch (Exception exception)
        {
            RaiseError(AutomationErrorSource.Trigger, "Stop trigger query loop", exception);
        }

        _triggerLoopTask = null;
    }

    private void StartReversionQueryLoop()
    {
        if (Interlocked.CompareExchange(ref _reversionLoopRunning, 1, 0) != 0)
        {
            return;
        }

        _reversionLoopTask = Task.Run(() => RunReversionQueryLoopAsync(_reversionLoopSource.Token));
    }

    /// <summary>
    /// 轮询等待回滚的运行器的规则集，并在规则集不再匹配时立即开始回滚。
    /// 同一时间只能运行一个循环。
    /// </summary>
    public async Task ReversionQueryLoop(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (Interlocked.CompareExchange(ref _reversionLoopRunning, 1, 0) != 0)
        {
            throw new InvalidOperationException("The reversion query loop is already running.");
        }

        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _reversionLoopSource.Token);

        await RunReversionQueryLoopAsync(linkedSource.Token).ConfigureAwait(false);
    }

    private async Task RunReversionQueryLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(ReversionPollInterval);

            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                // Keys 返回快照，因此在循环期间注册/注销是安全的。
                var runners = _reversionRunners.Keys.ToArray();
                if (runners.Length == 0)
                {
                    continue;
                }

                // 并发检查规则集，避免某个缓慢的规则集拖慢其他规则集。
                await Parallel.ForEachAsync(
                    runners,
                    new ParallelOptions
                    {
                        CancellationToken = cancellationToken,
                        MaxDegreeOfParallelism = ReversionCheckConcurrency
                    },
                    CheckReversionAsync).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 服务关闭时属于预期情况。
        }
        catch (Exception exception)
        {
            RaiseError(AutomationErrorSource.Ruleset, "Run reversion query loop", exception);
        }
        finally
        {
            Volatile.Write(ref _reversionLoopRunning, 0);
        }
    }

    private async ValueTask CheckReversionAsync(
        WorkflowRunner runner,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        if (runner.Status is not WorkflowRunnerStatus.WaitingForReversion)
        {
            _reversionRunners.TryRemove(runner, out _);
            return;
        }

        bool rulesetMatches;
        try
        {
            rulesetMatches = await runner.Ruleset
                .ValidateAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            // 单个规则集出错不应停止循环；继续轮询并重试。
            RaiseError(
                AutomationErrorSource.Ruleset,
                $"Validate ruleset of runner '{runner.Guid}'",
                exception,
                runner);
            return;
        }

        if (rulesetMatches || cancellationToken.IsCancellationRequested)
        {
            return;
        }

        // 在派发前先移除，确保同一段等待期只启动一次回滚。
        if (_reversionRunners.TryRemove(runner, out _))
        {
            TriggerReversion(runner);
        }
    }

    private void TriggerReversion(WorkflowRunner runner)
    {
        // 此处不要等待：回滚可能耗时较长，不能阻塞其他运行器的轮询循环。
        // OnReversionTriggered 会自行再次检查运行器状态。
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _reversionTasks.TryAdd(completion.Task, 0);

        _ = Task.Run(async () =>
        {
            try
            {
                await runner.OnReversionTriggered(this, EventArgs.Empty).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                RaiseError(
                    AutomationErrorSource.Workflow,
                    $"Revert runner '{runner.Guid}'",
                    exception,
                    runner);
            }
            finally
            {
                _reversionTasks.TryRemove(completion.Task, out _);
                completion.TrySetResult();
            }
        });
    }

    /// <summary>
    /// 停止由 <see cref="InitializeAsync"/> 启动的回滚查询循环，并等待其结束。
    /// </summary>
    public async Task StopReversionQueryLoopAsync()
    {
        await _reversionLoopSource.CancelAsync().ConfigureAwait(false);

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
            // 服务关闭时属于预期情况。
        }
        catch (Exception exception)
        {
            RaiseError(AutomationErrorSource.Ruleset, "Stop reversion query loop", exception);
        }

        _reversionLoopTask = null;
    }

    /// <summary>
    /// 当 <see cref="Workflow.IsEnabled"/> 变化时，添加或移除工作流的运行器。
    /// </summary>
    private void OnWorkflowIsEnabledChanged(object? sender, EventArgs e)
    {
        _ = HandleWorkflowIsEnabledChangedAsync(sender);
    }

    private async Task HandleWorkflowIsEnabledChangedAsync(object? sender)
    {
        try
        {
            // 属性 setter 不应等待工作流初始化完成。
            await Task.Yield();

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
            // 事件由属性 setter 引发；异常逃逸会导致进程崩溃。
            RaiseError(AutomationErrorSource.Workflow, "Handle workflow enabled change", exception);
        }
    }

    /// <summary>
    /// 为工作流创建并初始化运行器，并注册其触发器。
    /// 同一工作流已存在运行器时不执行任何操作。
    /// </summary>
    /// <param name="workflow">要运行的工作流。</param>
    public async Task AddWorkflowRunner(Workflow workflow)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        ThrowIfDisposed();

        WorkflowRunner runner;
        TaskCompletionSource initializationCompletion;

        await _runnerLock.WaitAsync().ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();

            if (_runners.Any(x => x.Workflow.Guid == workflow.Guid))
            {
                return;
            }

            runner = new WorkflowRunner(workflow, GetOrBuildServiceProvider());
            initializationCompletion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);

            _runners.Add(runner);
            _runnerInitializations[runner] = initializationCompletion.Task;
            runner.StatusChanged += OnWorkflowRunnerStatusChanged;
            runner.ErrorOccurred += OnWorkflowRunnerErrorOccurred;
        }
        finally
        {
            _runnerLock.Release();
        }

        var registered = false;

        try
        {
            await runner.InitializeAsync().ConfigureAwait(false);

            // 仅在初始化成功后注册：初始化失败时运行器会清空其触发器集合，
            // 之后将无法找到这些触发器进行注销。
            // 锁可防止并发的 RemoveWorkflowRunner 在检查和注册之间销毁运行器，
            // 否则会遗留过期的触发器。
            await _runnerLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (Volatile.Read(ref _disposed) == 0 && _runners.Contains(runner))
                {
                    RegisterTriggers(runner);
                    registered = true;
                }
            }
            finally
            {
                _runnerLock.Release();
            }

            if (!registered)
            {
                // 初始化期间服务被销毁或工作流被禁用。
                try
                {
                    await DetachRunnerAsync(runner).ConfigureAwait(false);
                }
                catch (Exception cleanupException)
                {
                    RaiseError(
                        AutomationErrorSource.Disposal,
                        $"Detach runner '{runner.Guid}'",
                        cleanupException,
                        runner);
                }
            }
        }
        catch (Exception exception)
        {
            RaiseError(
                AutomationErrorSource.Workflow,
                $"Initialize workflow '{workflow.Name}'",
                exception,
                runner);

            try
            {
                await DetachRunnerAsync(runner).ConfigureAwait(false);
            }
            catch (Exception cleanupException)
            {
                RaiseError(
                    AutomationErrorSource.Disposal,
                    $"Detach runner '{runner.Guid}'",
                    cleanupException,
                    runner);
            }

            initializationCompletion.TrySetException(exception);
            throw;
        }
        finally
        {
            initializationCompletion.TrySetResult();
            _runnerInitializations.TryRemove(runner, out _);
        }
    }

    /// <summary>
    /// 释放工作流的运行器并注销其触发器。
    /// </summary>
    /// <param name="workflow">要移除运行器的工作流。</param>
    public async Task RemoveWorkflowRunner(Workflow workflow)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        ThrowIfDisposed();

        WorkflowRunner? runner;
        Task? initializationTask;

        try
        {
            await _runnerLock.WaitAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // 服务在移除过程中被销毁。
            return;
        }

        try
        {
            runner = _runners.FirstOrDefault(x => x.Workflow.Guid == workflow.Guid);
            if (runner is null)
            {
                return;
            }

            _runners.Remove(runner);
            _runnerInitializations.TryGetValue(runner, out initializationTask);
        }
        finally
        {
            _runnerLock.Release();
        }

        if (initializationTask is not null)
        {
            try
            {
                await initializationTask.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                RaiseError(
                    AutomationErrorSource.Workflow,
                    $"Wait for workflow '{workflow.Name}' initialization",
                    exception,
                    runner);
            }
        }

        await DetachRunnerAsync(runner).ConfigureAwait(false);
    }

    /// <summary>
    /// 跟踪等待回滚的运行器，并注销正在销毁的运行器的触发器。
    /// </summary>
    public void OnWorkflowRunnerStatusChanged(object? sender, WorkflowRunnerStatusChangedEventArgs e)
    {
        if (sender is not WorkflowRunner runner)
        {
            return;
        }

        // 仅在运行器等待其动作被回滚时对其进行监视。
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
                // 运行器在引发 Destroying 后会清空其触发器，因此现在注销。
                UnregisterTriggers(runner);
                runner.ErrorOccurred -= OnWorkflowRunnerErrorOccurred;
                break;
            default:
                return;
        }
    }

    private void OnWorkflowRunnerErrorOccurred(object? sender, AutomationErrorEventArgs e)
    {
        RaiseError(
            e.Source,
            e.Operation,
            e.Exception,
            e.Runner ?? sender as WorkflowRunner,
            e.Action,
            e.Trigger);
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

    private async Task DetachRunnerAsync(WorkflowRunner runner)
    {
        var lockTaken = false;

        try
        {
            await _runnerLock.WaitAsync().ConfigureAwait(false);
            lockTaken = true;
        }
        catch (ObjectDisposedException)
        {
            // 服务已经销毁；继续清理运行器自身。
        }

        try
        {
            if (lockTaken)
            {
                _runners.Remove(runner);
            }
        }
        finally
        {
            if (lockTaken)
            {
                _runnerLock.Release();
            }
        }

        runner.StatusChanged -= OnWorkflowRunnerStatusChanged;
        runner.ErrorOccurred -= OnWorkflowRunnerErrorOccurred;
        UnregisterTriggers(runner);
        _reversionRunners.TryRemove(runner, out _);

        try
        {
            await runner.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            RaiseError(
                AutomationErrorSource.Disposal,
                $"Dispose runner '{runner.Guid}'",
                exception,
                runner);
        }
    }

    /// <summary>
    /// 停止查询循环，销毁所有运行器并释放服务资源。
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // 等待正在进行的初始化完成，避免在构造运行器的过程中销毁它。
        var initializeTask = _initializeTask;
        if (initializeTask is not null)
        {
            try
            {
                await initializeTask.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                RaiseError(AutomationErrorSource.Disposal, "Wait for initialization", exception);
            }
        }

        await StopTriggerQueryLoopAsync().ConfigureAwait(false);
        await StopReversionQueryLoopAsync().ConfigureAwait(false);

        var initializationTasks = _runnerInitializations.Values.ToArray();
        if (initializationTasks.Length > 0)
        {
            try
            {
                await Task.WhenAll(initializationTasks).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                RaiseError(AutomationErrorSource.Disposal, "Wait for runner initializations", exception);
            }
        }

        await _runnerLock.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var runner in _runners.ToArray())
            {
                runner.StatusChanged -= OnWorkflowRunnerStatusChanged;
                runner.ErrorOccurred -= OnWorkflowRunnerErrorOccurred;

                try
                {
                    await runner.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    RaiseError(
                        AutomationErrorSource.Disposal,
                        $"Dispose runner '{runner.Guid}'",
                        exception,
                        runner);
                }
            }

            _runners.Clear();
        }
        finally
        {
            _runnerLock.Release();
        }

        // 会跟踪回滚任务，因此关闭过程不会与仍在运行的回滚竞争。
        // 上方的运行器销毁会中断它们，因此通常能很快完成。
        var reversionTasks = _reversionTasks.Keys.ToArray();
        if (reversionTasks.Length > 0)
        {
            try
            {
                await Task.WhenAll(reversionTasks).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                RaiseError(AutomationErrorSource.Disposal, "Wait for reversion tasks", exception);
            }
        }

        foreach (var workflow in _workflows)
        {
            workflow.IsEnabledChanged -= OnWorkflowIsEnabledChanged;
        }

        _workflows.Clear();
        _activeTriggers.Clear();
        _reversionRunners.Clear();
        _reversionTasks.Clear();
        _runnerInitializations.Clear();

        await DisposeOwnedServiceProviderAsync().ConfigureAwait(false);

        _triggerLoopSource.Dispose();
        _reversionLoopSource.Dispose();
        _runnerLock.Dispose();
        IsInitialized = false;
    }

    private IServiceProvider GetOrBuildServiceProvider()
    {
        lock (_providerLock)
        {
            if (_serviceProvider is not null)
            {
                return _serviceProvider;
            }

            _serviceProvider = _serviceCollection.BuildServiceProvider();
            _ownsServiceProvider = true;
            return _serviceProvider;
        }
    }

    private async ValueTask DisposeOwnedServiceProviderAsync()
    {
        IServiceProvider? serviceProvider;

        lock (_providerLock)
        {
            if (!_ownsServiceProvider)
            {
                return;
            }

            serviceProvider = _serviceProvider;
            _serviceProvider = null;
            _ownsServiceProvider = false;
        }

        try
        {
            if (serviceProvider is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            }
            else if (serviceProvider is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
        catch (Exception exception)
        {
            RaiseError(AutomationErrorSource.Disposal, "Dispose service provider", exception);
        }
    }

    private void RaiseError(
        AutomationErrorSource source,
        string operation,
        Exception exception,
        WorkflowRunner? runner = null,
        ActionBase? action = null,
        TriggerBase? trigger = null)
    {
        Debug.WriteLine($"[AutomationService] {operation} failed: {exception}");

        var handlers = ErrorOccurred;
        if (handlers is null)
        {
            return;
        }

        var eventArgs = new AutomationErrorEventArgs(
            source,
            operation,
            exception,
            runner,
            action,
            trigger);

        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((EventHandler<AutomationErrorEventArgs>)handler)(this, eventArgs);
            }
            catch (Exception handlerException)
            {
                // 错误订阅者自身出错时不能破坏服务状态机。
                Debug.WriteLine(handlerException);
            }
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }

    private static TimeSpan ValidateInterval(TimeSpan value, string propertyName)
    {
        if (value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                propertyName,
                value,
                "The polling interval must be greater than zero.");
        }

        return value;
    }
}
