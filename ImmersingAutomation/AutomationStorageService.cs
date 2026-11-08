using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ImmersingAutomation.Abstractions;
using ImmersingAutomation.Enums;
using ImmersingAutomation.Events;
using ImmersingAutomation.Models;
using Microsoft.Extensions.DependencyInjection;

namespace ImmersingAutomation;

/// <summary>
/// 将工作流以 JSON 文件的形式存储在目录中，并使用不可变的
/// <see cref="AutomationTypeRegistry"/> 还原多态配置。
/// </summary>
public sealed class AutomationStorageService : IAutomationStorageService
{
    private const string WorkflowFileExtension = ".json";
    private const string TypePropertyName = "$type";

    private readonly JsonSerializerOptions _jsonSerializerOptions;

    /// <summary>
    /// 初始化 <see cref="AutomationStorageService"/> 类的新实例。
    /// </summary>
    /// <param name="typeRegistry">用于序列化多态配置的类型注册表。</param>
    /// <param name="storagePath">存储工作流的目录。</param>
    public AutomationStorageService(AutomationTypeRegistry typeRegistry, string storagePath)
    {
        ArgumentNullException.ThrowIfNull(typeRegistry);
        ArgumentException.ThrowIfNullOrWhiteSpace(storagePath);

        _jsonSerializerOptions = CreateJsonSerializerOptions(typeRegistry);
        StoragePath = Path.GetFullPath(storagePath);
    }

    /// <summary>
    /// 从服务集合中的自动化类型注册项创建存储服务。
    /// </summary>
    /// <param name="services">包含自动化类型注册项的服务集合。</param>
    /// <param name="storagePath">存储工作流的目录。</param>
    /// <remarks>
    /// 注册表在构造时创建，因此之后向 <paramref name="services"/> 添加的类型不会被此实例识别。
    /// </remarks>
    public AutomationStorageService(IServiceCollection services, string storagePath)
        : this(AutomationTypeRegistry.Create(services), storagePath)
    {
    }

    /// <summary>
    /// 当工作流文件无法加载或保存时发生。
    /// </summary>
    public event EventHandler<AutomationErrorEventArgs>? ErrorOccurred;

    /// <summary>
    /// 获取存储工作流的绝对目录。
    /// </summary>
    public string StoragePath { get; }

    /// <summary>
    /// 使用已注册的自动化类型将工作流序列化为 JSON。
    /// </summary>
    /// <param name="workflow">要序列化的工作流。</param>
    /// <returns><paramref name="workflow"/> 的 JSON 表示。</returns>
    public string SerializeWorkflow(Workflow workflow)
    {
        ArgumentNullException.ThrowIfNull(workflow);

        return JsonSerializer.Serialize(workflow, _jsonSerializerOptions);
    }

    /// <summary>
    /// 使用已注册的自动化类型从 JSON 反序列化工作流。
    /// </summary>
    /// <param name="json">工作流的 JSON 表示。</param>
    /// <returns>反序列化后的工作流。</returns>
    public Workflow DeserializeWorkflow(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        var workflow = JsonSerializer.Deserialize<Workflow>(json, _jsonSerializerOptions)
                       ?? throw new JsonException("Failed to deserialize workflow.");

        if (workflow.SchemaVersion is <= 0 or > Workflow.CurrentSchemaVersion)
        {
            throw new JsonException(
                $"Unsupported workflow schema version '{workflow.SchemaVersion}'. " +
                $"The supported version is '{Workflow.CurrentSchemaVersion}'.");
        }

        ValidateWorkflow(workflow);

        return workflow;
    }

    private static void ValidateWorkflow(Workflow workflow)
    {
        // 显式的 null 会覆盖属性初始值，因此必须在反序列化后校验，
        // 避免运行期出现难以定位的 NullReferenceException。
        if (workflow.Name is null)
        {
            throw new JsonException($"Workflow property '{nameof(Workflow.Name)}' cannot be null.");
        }

        if (workflow.TriggerProfiles is null)
        {
            throw new JsonException(
                $"Workflow property '{nameof(Workflow.TriggerProfiles)}' cannot be null.");
        }

        if (workflow.ActionProfiles is null)
        {
            throw new JsonException(
                $"Workflow property '{nameof(Workflow.ActionProfiles)}' cannot be null.");
        }

        if (workflow.RulesetProfile is null)
        {
            throw new JsonException(
                $"Workflow property '{nameof(Workflow.RulesetProfile)}' cannot be null.");
        }

        if (workflow.RulesetProfile.Rules is null)
        {
            throw new JsonException(
                $"Workflow property '{nameof(Workflow.RulesetProfile)}.{nameof(RulesetProfile.Rules)}' " +
                "cannot be null.");
        }

        if (workflow.TriggerProfiles.Any(static profile => profile is null))
        {
            throw new JsonException(
                $"Workflow property '{nameof(Workflow.TriggerProfiles)}' cannot contain null entries.");
        }

        if (workflow.ActionProfiles.Any(static profile => profile is null))
        {
            throw new JsonException(
                $"Workflow property '{nameof(Workflow.ActionProfiles)}' cannot contain null entries.");
        }

        if (workflow.RulesetProfile.Rules.Any(static rule => rule is null))
        {
            throw new JsonException(
                $"Workflow property '{nameof(Workflow.RulesetProfile)}.{nameof(RulesetProfile.Rules)}' " +
                "cannot contain null entries.");
        }
    }

    /// <summary>
    /// 序列化工作流并写入其文件，以原子方式替换之前的内容。
    /// </summary>
    /// <param name="workflow">要保存的工作流。</param>
    /// <param name="cancellationToken">用于取消操作的标记。</param>
    public async Task SaveWorkflowAsync(Workflow workflow, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workflow);

        Directory.CreateDirectory(StoragePath);

        var filePath = GetWorkflowFilePath(workflow.Guid);
        var temporaryFilePath = $"{filePath}.{Guid.NewGuid():N}.tmp";
        var json = SerializeWorkflow(workflow);

        try
        {
            await File.WriteAllTextAsync(temporaryFilePath, json, Encoding.UTF8, cancellationToken)
                .ConfigureAwait(false);
            File.Move(temporaryFilePath, filePath, overwrite: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryDelete(temporaryFilePath);
            throw;
        }
        catch (Exception exception)
        {
            TryDelete(temporaryFilePath);
            RaiseError("Save workflow", exception);
            throw;
        }
    }

    /// <summary>
    /// 从其文件加载指定标识符的工作流。
    /// </summary>
    /// <param name="workflowId">工作流的标识符。</param>
    /// <param name="cancellationToken">用于取消操作的标记。</param>
    /// <returns>工作流；文件不存在时返回 <see langword="null"/>。</returns>
    public async Task<Workflow?> LoadWorkflowAsync(Guid workflowId, CancellationToken cancellationToken = default)
    {
        var filePath = GetWorkflowFilePath(workflowId);
        if (!File.Exists(filePath))
        {
            return null;
        }

        var json = await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
        return DeserializeWorkflow(json);
    }

    /// <summary>
    /// 加载 <see cref="StoragePath"/> 中的每个有效工作流文件。
    /// </summary>
    /// <param name="cancellationToken">用于取消操作的标记。</param>
    /// <returns>已存储的工作流；目录不存在时返回空列表。</returns>
    /// <remarks>
    /// 单个损坏的工作流文件会被跳过并引发 <see cref="ErrorOccurred"/>，
    /// 不会阻止其他工作流加载。
    /// </remarks>
    public async Task<IReadOnlyList<Workflow>> LoadWorkflowsAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(StoragePath))
        {
            return [];
        }

        var workflows = new List<Workflow>();

        foreach (var filePath in Directory.EnumerateFiles(StoragePath, $"*{WorkflowFileExtension}"))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                var json = await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
                workflows.Add(DeserializeWorkflow(json));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                RaiseError($"Load workflow file '{filePath}'", exception);
            }
        }

        return workflows;
    }

    /// <summary>
    /// 确定工作流的文件是否存在。
    /// </summary>
    /// <param name="workflowId">工作流的标识符。</param>
    public bool WorkflowExists(Guid workflowId)
    {
        return File.Exists(GetWorkflowFilePath(workflowId));
    }

    /// <summary>
    /// 删除工作流的文件。
    /// </summary>
    /// <param name="workflowId">工作流的标识符。</param>
    /// <param name="cancellationToken">用于取消操作的标记。</param>
    /// <returns>文件存在且已删除时返回 <see langword="true"/>。</returns>
    public Task<bool> DeleteWorkflowAsync(Guid workflowId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var filePath = GetWorkflowFilePath(workflowId);
        if (!File.Exists(filePath))
        {
            return Task.FromResult(false);
        }

        File.Delete(filePath);
        return Task.FromResult(true);
    }

    private string GetWorkflowFilePath(Guid workflowId)
    {
        return Path.Combine(StoragePath, $"{workflowId:D}{WorkflowFileExtension}");
    }

    private static JsonSerializerOptions CreateJsonSerializerOptions(AutomationTypeRegistry typeRegistry)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        // 使用字符串枚举，避免枚举成员重排后改变已存储数据的含义。
        options.Converters.Add(new JsonStringEnumConverter());
        options.Converters.Add(new AutomationPolymorphicConverterFactory(typeRegistry));

        return options;
    }

    private static void TryDelete(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
        }
    }

    private void RaiseError(string operation, Exception exception)
    {
        Debug.WriteLine($"[AutomationStorageService] {operation} failed: {exception}");

        var handlers = ErrorOccurred;
        if (handlers is null)
        {
            return;
        }

        var eventArgs = new AutomationErrorEventArgs(
            AutomationErrorSource.Storage,
            operation,
            exception);

        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((EventHandler<AutomationErrorEventArgs>)handler)(this, eventArgs);
            }
            catch (Exception handlerException)
            {
                Debug.WriteLine(handlerException);
            }
        }
    }

    private sealed class AutomationPolymorphicConverterFactory : JsonConverterFactory
    {
        private readonly AutomationTypeRegistry _typeRegistry;

        public AutomationPolymorphicConverterFactory(AutomationTypeRegistry typeRegistry)
        {
            _typeRegistry = typeRegistry;
        }

        public override bool CanConvert(Type typeToConvert)
        {
            return typeToConvert == typeof(TriggerProfileBase)
                   || typeToConvert == typeof(ActionProfileBase)
                   || typeToConvert == typeof(RuleProfileBase)
                   || typeToConvert == typeof(RuleBase);
        }

        public override JsonConverter? CreateConverter(Type typeToConvert, JsonSerializerOptions options)
        {
            if (typeToConvert == typeof(TriggerProfileBase))
            {
                return new AutomationPolymorphicConverter<TriggerProfileBase>(_typeRegistry);
            }

            if (typeToConvert == typeof(ActionProfileBase))
            {
                return new AutomationPolymorphicConverter<ActionProfileBase>(_typeRegistry);
            }

            if (typeToConvert == typeof(RuleProfileBase))
            {
                return new AutomationPolymorphicConverter<RuleProfileBase>(_typeRegistry);
            }

            if (typeToConvert == typeof(RuleBase))
            {
                return new AutomationPolymorphicConverter<RuleBase>(_typeRegistry);
            }

            throw new NotSupportedException($"Unsupported polymorphic automation type '{typeToConvert}'.");
        }
    }

    private sealed class AutomationPolymorphicConverter<TBase> : JsonConverter<TBase> where TBase : class
    {
        private readonly AutomationTypeRegistry _typeRegistry;

        public AutomationPolymorphicConverter(AutomationTypeRegistry typeRegistry)
        {
            _typeRegistry = typeRegistry;
        }

        public override void Write(Utf8JsonWriter writer, TBase value, JsonSerializerOptions options)
        {
            var concreteType = value.GetType();
            var typeId = _typeRegistry.GetTypeId<TBase>(concreteType);
            var element = JsonSerializer.SerializeToElement(value, concreteType, options);

            writer.WriteStartObject();
            writer.WriteString(TypePropertyName, typeId.ToString("D"));

            foreach (var property in element.EnumerateObject())
            {
                if (property.NameEquals(TypePropertyName))
                {
                    continue;
                }

                property.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        public override TBase? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            var element = document.RootElement;

            if (element.ValueKind != JsonValueKind.Object
                || !element.TryGetProperty(TypePropertyName, out var typeProperty)
                || typeProperty.ValueKind != JsonValueKind.String
                || !Guid.TryParse(typeProperty.GetString(), out var typeId))
            {
                throw new JsonException(
                    $"Unable to read the '{TypePropertyName}' identifier for '{typeof(TBase).Name}'.");
            }

            var concreteType = _typeRegistry.GetConcreteType<TBase>(typeId);

            return (TBase?)JsonSerializer.Deserialize(element.GetRawText(), concreteType, options);
        }
    }
}
