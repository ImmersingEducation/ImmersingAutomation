# ImmersingAutomation

基于**触发器（Trigger）→ 规则集（Ruleset）→ 动作（Action）→ 回滚（Reversion）** 的可扩展 .NET 工作流自动化引擎。

[![NuGet](https://img.shields.io/badge/nuget-0.1.0--beta.1-blue)](https://www.nuget.org/packages/ImmersingAutomation)
[![License: MIT](https://img.shields.io/badge/license-MIT-green)](LICENSE)

> **Beta 提示**：`0.1.0-beta.1` 是技术预览版本，公共 API 可能在正式版前调整。欢迎通过 Issue 反馈问题与使用场景。

## 特性

- **可插拔自动化类型**：通过特性（Attribute）注册触发器、规则与动作，并持久化为 JSON。
- **状态机驱动**：触发器、动作与工作流运行器都有明确的生命周期状态，中断可安全地贯穿运行与回滚。
- **回滚与中断**：动作声明是否支持回滚；规则集不再匹配时自动回滚，运行中也可随时 `Interrupt()`。
- **异常处理策略**：按工作流选择 `Skip`（跳过失败动作继续）或 `Interrupt`（中断并回滚）。
- **并发轮询**：触发器与回滚检查均可配置并发度，单个慢速实现不会阻塞其他工作流。
- **错误隔离**：任何后台失败都通过 `ErrorOccurred` 事件上报，不会破坏其他工作流的状态机。
- **依赖注入**：触发器/动作的构造函数依赖由 `IServiceProvider` 解析。
- **可靠存储**：工作流以原子写方式保存为 JSON，支持 Schema 版本与多态类型标识。

## 安装

```bash
dotnet add package ImmersingAutomation --version 0.1.0-beta.1
```

目标框架：`net10.0`。

## 核心概念

| 概念 | 说明 |
| --- | --- |
| `TriggerBase` | 轮询型触发器。重写 `Validate()` 返回条件是否满足；基类负责状态机与串行化。 |
| `RuleBase` | 规则条件。可组合为 `Ruleset`，使用 `All` 或 `Any` 模式求值，并支持 `IsReversed`。 |
| `ActionBase` | 可运行、可选回滚的动作。重写 `_RunAsync()` 与 `_RevertAsync()`。 |
| `Workflow` | 描述一个自动化：触发器配置、规则集、动作列表、是否启用回滚与异常处理模式。 |
| `WorkflowRunner` | 执行单个工作流，驱动其状态转换。 |
| `AutomationService` | 加载已存储的工作流、启动轮询循环，并管理所有运行器。 |
| `AutomationStorageService` | 将工作流以 JSON 文件保存在目录中，并还原多态配置。 |

## 快速开始

下面实现一个“文件出现后复制到目标位置”的最小工作流。

### 1. 定义自动化类型（`AutomationTypes.cs`）

```csharp
using ImmersingAutomation.Abstractions;
using ImmersingAutomation.Attributes;

// 触发器配置 + 触发器实现
[TriggerProfile(typeof(FileExistsTrigger))]
public sealed class FileExistsTriggerProfile : TriggerProfileBase
{
    public string Path { get; set; } = string.Empty;
}

[Trigger("6f9619ff-8b86-d011-b42d-00cf4fc964ff", "文件出现")]
public sealed class FileExistsTrigger : TriggerBase<FileExistsTriggerProfile>
{
    public override bool Validate() => File.Exists(Profile.Path);

    protected override Task _InitializeAsync() => Task.CompletedTask;

    protected override Task _ReleaseAsync() => Task.CompletedTask;
}

// 动作配置 + 动作实现（声明支持回滚）
[ActionProfile(typeof(CopyFileAction))]
public sealed class CopyFileActionProfile : ActionProfileBase
{
    public string Source { get; set; } = string.Empty;
    public string Destination { get; set; } = string.Empty;
}

[Action("8f9619ff-8b86-d011-b42d-00cf4fc964ff", "复制文件", isReversionSupported: true)]
public sealed class CopyFileAction : ActionBase<CopyFileActionProfile>
{
    protected override Task _RunAsync()
    {
        ThrowIfInterrupted();
        File.Copy(Profile.Source, Profile.Destination, overwrite: true);
        return Task.CompletedTask;
    }

    protected override Task _RevertAsync()
    {
        if (File.Exists(Profile.Destination))
        {
            File.Delete(Profile.Destination);
        }

        return Task.CompletedTask;
    }
}
```

### 2. 注册类型并启动服务（`Program.cs`）

```csharp
using ImmersingAutomation;
using ImmersingAutomation.Enums;
using ImmersingAutomation.Models;
using Microsoft.Extensions.DependencyInjection;

// 注册类型、保存工作流并启动服务
var services = new ServiceCollection()
    .AddSingleton<FileExistsTrigger>()
    .AddSingleton<CopyFileAction>();

var storage = new AutomationStorageService(services, "./workflows");

var workflow = new Workflow
{
    Name = "文件出现后复制",
    IsEnabled = true,
    TriggerProfiles = [new FileExistsTriggerProfile { Path = "./input.txt" }],
    // 空的 All 规则集恒为 true；如需限制执行条件，可在此添加 RuleBase 实例
    RulesetProfile = new RulesetProfile { Mode = RulesetMode.All },
    ActionProfiles =
    [
        new CopyFileActionProfile
        {
            Source = "./input.txt",
            Destination = "./output.txt"
        }
    ]
};

await storage.SaveWorkflowAsync(workflow);

await using var automation = new AutomationService(storage, services)
{
    TriggerPollInterval = TimeSpan.FromMilliseconds(500)
};

automation.ErrorOccurred += (_, e) =>
    Console.WriteLine($"[{e.Source}] {e.Operation}: {e.Exception.Message}");

await automation.InitializeAsync();

// ... 让服务持续运行（例如 await Task.Delay(Timeout.Infinite)）
```

### 启用回滚

将工作流设置为 `IsReversionEnabled = true` 后，动作执行完毕会进入 `WaitingForReversion`。
`AutomationService` 会持续验证规则集：**当规则集不再匹配时**，按执行顺序的逆序调用 `_RevertAsync()`。

```csharp
var workflow = new Workflow
{
    // ...
    IsReversionEnabled = true,
    RulesetProfile = new RulesetProfile
    {
        Mode = RulesetMode.All,
        Rules = [/* 例如：目标文件仍存在时保持，删除后触发回滚 */]
    }
};
```

### 中断

```csharp
// 运行中、规则集验证中或回滚中都可通过运行器请求中断
runner.Interrupt();
```

中断会取消当前动作的 `InterruptionToken`，并根据状态从正确的位置开始回滚或直接复位。

## 持久化

- 每个工作流保存为 `<Workflow.Guid>.json`，写入使用临时文件 + 原子替换。
- 反序列化使用 `$type` 字段保存的类型 Guid 还原多态配置，枚举以字符串存储。
- 单个损坏的文件会在加载时被跳过并通过 `ErrorOccurred` 上报，不影响其他工作流。
- 类型注册表在创建后不可变；新增自动化类型后需要重新创建 `AutomationStorageService`。

## 已知限制（Beta）

- **触发事件不排队**：工作流处理触发期间到达的触发会被丢弃（`WorkflowRunner` 使用单标志位去重）。对电平型触发器（条件持续为真）无影响；对消费型/边沿型触发器请自行在触发器中缓冲。
- **动作没有释放钩子**：`ActionBase` 暂未实现 `IAsyncDisposable`，动作持有的资源应在 `_RevertAsync()` 或自身逻辑中释放。
- **轮询间隔与并发度为初始化属性**：`TriggerPollInterval`、`ReversionPollInterval`、`*CheckConcurrency` 仅在构造/初始化对象时设置，运行期不可修改。
- **仅面向 `net10.0`**。

## 构建与测试

```bash
dotnet build
dotnet test
dotnet pack ImmersingAutomation/ImmersingAutomation.csproj -c Release -o ./artifacts
```

## 许可证

[MIT](LICENSE) © 2026 ImmersingEducation
