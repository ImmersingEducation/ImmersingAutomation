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
    private readonly SemaphoreSlim _runnerLock = new(1, 1);
    private readonly CancellationTokenSource _triggerLoopSource = new();
    private readonly CancellationTokenSource _reversionLoopSource = new();

    private IServiceCollection _serviceCollection = new ServiceCollection();
    private IServiceProvider _serviceProvider;
    private IAutomationStorageService _storageService;
    private List<Workflow> _workflows = [];
    private List<WorkflowRunner> _runners = [];
    private Task? _triggerLoopTask;
    private Task? _reversionLoopTask;
    private int _triggerLoopRunning;
    private int _reversionLoopRunning;
    private int _initializeStarted;
    private int _disposed;

    /// <summary>
    /// 获取服务是否已初始化。
    /// </summary>
    public bool IsInitialized { get; private set; } = false;

    /// <summary>
    /// 两次轮询已注册触发器之间的间隔。
    /// </summary>
    public TimeSpan TriggerPollInterval { get; init; } = TimeSpan.FromMilliseconds(20);

    /// <summary>
    /// 两次检查等待回滚的规则集之间的间隔。
    /// </summary>
    public TimeSpan ReversionPollInterval { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// 单次回滚轮询中并发验证的规则集最大数量。
    /// </summary>
    public int ReversionCheckConcurrency { get; init; } = 4;

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
    /// 设置提供程序也会将服务标记为已初始化。
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

            _serviceProvider = value;
            IsInitialized = true;
        }
    }

    internal IEnumerable<ServiceDescriptor> ServiceDescriptors => _serviceCollection;

    /// <summary>
    /// 初始化 <see cref="AutomationService"/> 类的新实例。
    /// </summary>
    /// <param name="storageService">用于加载和持久化工作流的存储服务。</param>
    public AutomationService(IAutomationStorageService storageService)
    {
        _storageService = storageService;
    }

    /// <summary>
    /// 加载已存储的工作流，启动已启用工作流的运行器，并启动触发器和回滚查询循环。
    /// 只有首次调用有效。
    /// </summary>
    public async Task InitializeAsync()
    {
        if (Interlocked.Exchange(ref _initializeStarted, 1) != 0)
        {
            return;
        }

        try
        {
            // 保留外部提供的提供程序；仅在未注入时才构建一个。
            _serviceProvider ??= _serviceCollection.BuildServiceProvider();
            _workflows = (await _storageService.LoadWorkflowsAsync().ConfigureAwait(false)).ToList();

            foreach (var workflow in _workflows)
            {
                // 初始化失败后可能会重试，因此不要重复订阅。
                workflow.IsEnabledChanged -= OnWorkflowIsEnabledChanged;
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
        catch
        {
            Volatile.Write(ref _initializeStarted, 0);
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
                        Debug.WriteLine(
                            $"[AutomationService] Trigger '{trigger.GetType().FullName}' failed: {exception}");
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 服务关闭时属于预期情况。
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

        _reversionLoopTask = Task.Run(() => RunReversionQueryLoopAsync(_reversionLoopSource.Token));
    }

    /// <summary>
    /// 轮询等待回滚的运行器的规则集，并在规则集不再匹配时立即开始回滚。
    /// 同一时间只能运行一个循环。
    /// </summary>
    public async Task ReversionQueryLoop(CancellationToken cancellationToken = default)
    {
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
                        MaxDegreeOfParallelism = Math.Max(1, ReversionCheckConcurrency)
                    },
                    CheckReversionAsync).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 服务关闭时属于预期情况。
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
            rulesetMatches = await runner.Ruleset.ValidateAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // 单个规则集出错不应停止循环；继续轮询并重试。
            Debug.WriteLine(
                $"[AutomationService] Ruleset of runner '{runner.Guid}' failed: {exception}");
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
                Debug.WriteLine(
                    $"[AutomationService] Reversion of runner '{runner.Guid}' failed: {exception}");
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
            Debug.WriteLine(exception);
        }

        _reversionLoopTask = null;
    }

    /// <summary>
    /// 当 <see cref="Workflow.IsEnabled"/> 变化时，添加或移除工作流的运行器。
    /// </summary>
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
            // 事件由属性 setter 引发；异常逃逸会导致进程崩溃。
            Debug.WriteLine(exception);
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

            // 仅在初始化成功后注册：初始化失败时运行器会清空其触发器集合，
            // 之后将无法找到这些触发器进行注销。
            // 锁可防止并发的 RemoveWorkflowRunner 在检查和注册之间销毁运行器，
            // 否则会遗留过期的触发器。
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
            _reversionRunners.TryRemove(runner, out _);

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

    /// <summary>
    /// 释放工作流的运行器并注销其触发器。
    /// </summary>
    /// <param name="workflow">要移除运行器的工作流。</param>
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
            // DisposeAsync 会引发 Destroying，但也要确保即使它抛出异常也不会留下已注册项。
            UnregisterTriggers(runner);
            _reversionRunners.TryRemove(runner, out _);
            runner.StatusChanged -= OnWorkflowRunnerStatusChanged;
        }
    }

    /// <summary>
    /// 注册在服务外部初始化的运行器的触发器。
    /// </summary>
    /// <param name="runner">要注册的已初始化运行器。</param>
    public Task ExternallyInitializeWorkflowRunnerAsync(WorkflowRunner runner)
    {
        ArgumentNullException.ThrowIfNull(runner);

        RegisterTriggers(runner);
        return Task.CompletedTask;
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

    /// <summary>
    /// 停止查询循环，销毁所有运行器并释放服务资源。
    /// </summary>
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
                Debug.WriteLine(exception);
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
        _triggerLoopSource.Dispose();
        _reversionLoopSource.Dispose();
        _runnerLock.Dispose();
    }
}
