using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ImmersingAutomation.Abstractions;
using ImmersingAutomation.Attributes;
using ImmersingAutomation.Models;
using Microsoft.Extensions.DependencyInjection;

namespace ImmersingAutomation;

/// <summary>
/// 将工作流以 JSON 文件的形式存储在目录中，并使用关联 <see cref="AutomationService"/>
/// 中注册的自动化类型进行还原。
/// </summary>
public sealed class AutomationStorageService : IAutomationStorageService
{
    private const string WorkflowFileExtension = ".json";
    private const string TypePropertyName = "$type";

    private readonly AutomationService _automationService;

    /// <summary>
    /// 初始化 <see cref="AutomationStorageService"/> 类的新实例。
    /// </summary>
    /// <param name="automationService">其注册类型用于序列化的服务。</param>
    /// <param name="storagePath">存储工作流的目录。</param>
    public AutomationStorageService(AutomationService automationService, string storagePath)
    {
        ArgumentNullException.ThrowIfNull(automationService);
        ArgumentException.ThrowIfNullOrWhiteSpace(storagePath);

        _automationService = automationService;
        StoragePath = Path.GetFullPath(storagePath);
    }

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

        return SerializeWorkflow(workflow, CreateJsonSerializerOptions());
    }

    /// <summary>
    /// 使用已注册的自动化类型从 JSON 反序列化工作流。
    /// </summary>
    /// <param name="json">工作流的 JSON 表示。</param>
    /// <returns>反序列化后的工作流。</returns>
    public Workflow DeserializeWorkflow(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        return DeserializeWorkflow(json, CreateJsonSerializerOptions());
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
        var temporaryFilePath = $"{filePath}.tmp";
        var json = SerializeWorkflow(workflow, CreateJsonSerializerOptions());

        try
        {
            await File.WriteAllTextAsync(temporaryFilePath, json, Encoding.UTF8, cancellationToken);
            File.Move(temporaryFilePath, filePath, overwrite: true);
        }
        catch
        {
            if (File.Exists(temporaryFilePath))
            {
                File.Delete(temporaryFilePath);
            }

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

        var json = await File.ReadAllTextAsync(filePath, cancellationToken);
        return DeserializeWorkflow(json, CreateJsonSerializerOptions());
    }

    /// <summary>
    /// 加载 <see cref="StoragePath"/> 中的每个工作流文件。
    /// </summary>
    /// <param name="cancellationToken">用于取消操作的标记。</param>
    /// <returns>已存储的工作流；目录不存在时返回空列表。</returns>
    public async Task<IReadOnlyList<Workflow>> LoadWorkflowsAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(StoragePath))
        {
            return [];
        }

        var workflows = new List<Workflow>();
        var jsonSerializerOptions = CreateJsonSerializerOptions();

        foreach (var filePath in Directory.EnumerateFiles(StoragePath, $"*{WorkflowFileExtension}"))
        {
            var json = await File.ReadAllTextAsync(filePath, cancellationToken);
            workflows.Add(DeserializeWorkflow(json, jsonSerializerOptions));
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

    private static string SerializeWorkflow(Workflow workflow, JsonSerializerOptions jsonSerializerOptions)
    {
        return JsonSerializer.Serialize(workflow, jsonSerializerOptions);
    }

    private static Workflow DeserializeWorkflow(string json, JsonSerializerOptions jsonSerializerOptions)
    {
        return JsonSerializer.Deserialize<Workflow>(json, jsonSerializerOptions)
               ?? throw new JsonException("Failed to deserialize workflow.");
    }

    private JsonSerializerOptions CreateJsonSerializerOptions()
    {
        var typeRegistry = AutomationTypeRegistry.Create(_automationService.ServiceDescriptors);

        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        options.Converters.Add(new AutomationPolymorphicConverterFactory(typeRegistry));

        return options;
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

    private sealed class AutomationTypeRegistry
    {
        private readonly Dictionary<Guid, Type> _triggerTypes = new();
        private readonly Dictionary<Guid, Type> _ruleTypes = new();
        private readonly Dictionary<Guid, Type> _actionTypes = new();

        private readonly Dictionary<Type, Guid> _triggerProfileTypeIds = new();
        private readonly Dictionary<Type, Guid> _actionProfileTypeIds = new();
        private readonly Dictionary<Type, Guid> _ruleProfileTypeIds = new();
        private readonly Dictionary<Type, Guid> _ruleTypeIds = new();

        private readonly Dictionary<Guid, Type> _triggerProfileTypes = new();
        private readonly Dictionary<Guid, Type> _actionProfileTypes = new();
        private readonly Dictionary<Guid, Type> _ruleProfileTypes = new();

        public static AutomationTypeRegistry Create(IEnumerable<ServiceDescriptor> descriptors)
        {
            var registry = new AutomationTypeRegistry();
            var implementationTypes = new HashSet<Type>();

            foreach (var descriptor in descriptors)
            {
                AddImplementationType(implementationTypes, descriptor.ImplementationType);
                AddImplementationType(implementationTypes, descriptor.ImplementationInstance?.GetType());
                AddImplementationType(implementationTypes, descriptor.ServiceType);
            }

            foreach (var implementationType in implementationTypes)
            {
                registry.Register(implementationType);
            }

            return registry;
        }

        private static void AddImplementationType(ISet<Type> implementationTypes, Type? implementationType)
        {
            if (implementationType is null
                || implementationType.IsAbstract
                || implementationType.IsInterface
                || implementationType.IsGenericTypeDefinition)
            {
                return;
            }

            implementationTypes.Add(implementationType);
        }

        private void Register(Type implementationType)
        {
            RegisterProfile(implementationType);

            var triggerAttribute = implementationType.GetCustomAttribute<TriggerAttribute>(inherit: false);
            if (triggerAttribute is not null && typeof(TriggerBase).IsAssignableFrom(implementationType))
            {
                Add(_triggerTypes, triggerAttribute.Guid, implementationType, "trigger");

                var profileType = GetGenericArgument(implementationType, typeof(TriggerBase<>));
                if (profileType is not null)
                {
                    Add(_triggerProfileTypeIds, profileType, triggerAttribute.Guid, "trigger profile");
                }
            }

            var ruleAttribute = implementationType.GetCustomAttribute<RuleAttribute>(inherit: false);
            if (ruleAttribute is not null && typeof(RuleBase).IsAssignableFrom(implementationType))
            {
                Add(_ruleTypes, ruleAttribute.Guid, implementationType, "rule");
                Add(_ruleTypeIds, implementationType, ruleAttribute.Guid, "rule");

                var profileType = GetGenericArgument(implementationType, typeof(RuleBase<>));
                if (profileType is not null)
                {
                    Add(_ruleProfileTypeIds, profileType, ruleAttribute.Guid, "rule profile");
                }
            }

            var actionAttribute = implementationType.GetCustomAttribute<ActionAttribute>(inherit: false);
            if (actionAttribute is not null && typeof(ActionBase).IsAssignableFrom(implementationType))
            {
                Add(_actionTypes, actionAttribute.Guid, implementationType, "action");

                var profileType = GetGenericArgument(implementationType, typeof(ActionBase<>));
                if (profileType is not null)
                {
                    Add(_actionProfileTypeIds, profileType, actionAttribute.Guid, "action profile");
                }
            }
        }

        private void RegisterProfile(Type implementationType)
        {
            if (typeof(TriggerProfileBase).IsAssignableFrom(implementationType))
            {
                var profileAttribute = implementationType.GetCustomAttribute<TriggerProfileAttribute>(inherit: false);
                if (profileAttribute is not null)
                {
                    var triggerAttribute = GetRequiredAttribute<TriggerAttribute>(
                        profileAttribute.TriggerType,
                        "trigger",
                        implementationType);

                    Add(_triggerProfileTypeIds, implementationType, triggerAttribute.Guid, "trigger profile");
                    Add(_triggerProfileTypes, triggerAttribute.Guid, implementationType, "trigger profile");
                }
            }

            if (typeof(ActionProfileBase).IsAssignableFrom(implementationType))
            {
                var profileAttribute = implementationType.GetCustomAttribute<ActionProfileAttribute>(inherit: false);
                if (profileAttribute is not null)
                {
                    var actionAttribute = GetRequiredAttribute<ActionAttribute>(
                        profileAttribute.ActionType,
                        "action",
                        implementationType);

                    Add(_actionProfileTypeIds, implementationType, actionAttribute.Guid, "action profile");
                    Add(_actionProfileTypes, actionAttribute.Guid, implementationType, "action profile");
                }
            }

            if (typeof(RuleProfileBase).IsAssignableFrom(implementationType))
            {
                var profileAttribute = implementationType.GetCustomAttribute<RuleProfileAttribute>(inherit: false);
                if (profileAttribute is not null)
                {
                    var ruleAttribute = GetRequiredAttribute<RuleAttribute>(
                        profileAttribute.RuleType,
                        "rule",
                        implementationType);

                    Add(_ruleProfileTypeIds, implementationType, ruleAttribute.Guid, "rule profile");
                    Add(_ruleProfileTypes, ruleAttribute.Guid, implementationType, "rule profile");
                }
            }
        }

        private static TAttribute GetRequiredAttribute<TAttribute>(
            Type targetType,
            string kind,
            Type profileType) where TAttribute : Attribute
        {
            return targetType.GetCustomAttribute<TAttribute>(inherit: false)
                   ?? throw new InvalidOperationException(
                       $"{kind} profile type '{profileType.FullName}' references {kind} type " +
                       $"'{targetType.FullName}' which has no [{typeof(TAttribute).Name}].");
        }

        public Guid GetTypeId<TBase>(Type concreteType) where TBase : class
        {
            var baseType = typeof(TBase);

            if (baseType == typeof(TriggerProfileBase))
            {
                if (_triggerProfileTypeIds.TryGetValue(concreteType, out var triggerTypeId))
                {
                    return triggerTypeId;
                }

                var profileAttribute = concreteType.GetCustomAttribute<TriggerProfileAttribute>(inherit: false);
                if (profileAttribute is not null)
                {
                    return GetRequiredAttribute<TriggerAttribute>(
                        profileAttribute.TriggerType,
                        "trigger",
                        concreteType).Guid;
                }

                var triggerAttribute = concreteType.GetCustomAttribute<TriggerAttribute>(inherit: false);
                if (triggerAttribute is not null)
                {
                    return triggerAttribute.Guid;
                }

                throw new InvalidOperationException(
                    $"Trigger profile type '{concreteType.FullName}' is not associated with a registered trigger. " +
                    $"Make sure the corresponding trigger implementation is registered and has a [{nameof(TriggerAttribute)}], " +
                    $"or annotate the profile with a [{nameof(TriggerProfileAttribute)}].");
            }

            if (baseType == typeof(ActionProfileBase))
            {
                if (_actionProfileTypeIds.TryGetValue(concreteType, out var actionTypeId))
                {
                    return actionTypeId;
                }

                var profileAttribute = concreteType.GetCustomAttribute<ActionProfileAttribute>(inherit: false);
                if (profileAttribute is not null)
                {
                    return GetRequiredAttribute<ActionAttribute>(
                        profileAttribute.ActionType,
                        "action",
                        concreteType).Guid;
                }

                var actionAttribute = concreteType.GetCustomAttribute<ActionAttribute>(inherit: false);
                if (actionAttribute is not null)
                {
                    return actionAttribute.Guid;
                }

                throw new InvalidOperationException(
                    $"Action profile type '{concreteType.FullName}' is not associated with a registered action. " +
                    $"Make sure the corresponding action implementation is registered and has a [{nameof(ActionAttribute)}], " +
                    $"or annotate the profile with a [{nameof(ActionProfileAttribute)}].");
            }

            if (baseType == typeof(RuleProfileBase))
            {
                if (_ruleProfileTypeIds.TryGetValue(concreteType, out var ruleTypeId))
                {
                    return ruleTypeId;
                }

                var profileAttribute = concreteType.GetCustomAttribute<RuleProfileAttribute>(inherit: false);
                if (profileAttribute is not null)
                {
                    return GetRequiredAttribute<RuleAttribute>(
                        profileAttribute.RuleType,
                        "rule",
                        concreteType).Guid;
                }

                throw new InvalidOperationException(
                    $"Rule profile type '{concreteType.FullName}' is not associated with a registered rule. " +
                    $"Make sure the corresponding rule implementation is registered and has a [{nameof(RuleAttribute)}], " +
                    $"or annotate the profile with a [{nameof(RuleProfileAttribute)}].");
            }

            if (baseType == typeof(RuleBase))
            {
                if (_ruleTypeIds.TryGetValue(concreteType, out var ruleTypeId))
                {
                    return ruleTypeId;
                }

                var ruleAttribute = concreteType.GetCustomAttribute<RuleAttribute>(inherit: false);
                if (ruleAttribute is not null)
                {
                    return ruleAttribute.Guid;
                }

                throw new InvalidOperationException(
                    $"Rule type '{concreteType.FullName}' is not registered and has no [{nameof(RuleAttribute)}].");
            }

            throw new NotSupportedException($"Unsupported polymorphic automation type '{baseType}'.");
        }

        public Type GetConcreteType<TBase>(Guid typeId) where TBase : class
        {
            var baseType = typeof(TBase);

            if (baseType == typeof(TriggerProfileBase))
            {
                return _triggerProfileTypes.TryGetValue(typeId, out var triggerProfileType)
                    ? triggerProfileType
                    : GetProfileType(_triggerTypes, typeId, typeof(TriggerBase<>), "trigger");
            }

            if (baseType == typeof(ActionProfileBase))
            {
                return _actionProfileTypes.TryGetValue(typeId, out var actionProfileType)
                    ? actionProfileType
                    : GetProfileType(_actionTypes, typeId, typeof(ActionBase<>), "action");
            }

            if (baseType == typeof(RuleProfileBase))
            {
                return _ruleProfileTypes.TryGetValue(typeId, out var ruleProfileType)
                    ? ruleProfileType
                    : GetProfileType(_ruleTypes, typeId, typeof(RuleBase<>), "rule");
            }

            if (baseType == typeof(RuleBase))
            {
                if (_ruleTypes.TryGetValue(typeId, out var ruleType))
                {
                    return ruleType;
                }

                throw new JsonException($"No rule implementation is registered for Guid '{typeId}'.");
            }

            throw new NotSupportedException($"Unsupported polymorphic automation type '{baseType}'.");
        }

        private static Type GetProfileType(
            IReadOnlyDictionary<Guid, Type> implementationTypes,
            Guid typeId,
            Type openGenericBaseType,
            string kind)
        {
            if (!implementationTypes.TryGetValue(typeId, out var implementationType))
            {
                throw new JsonException($"No {kind} implementation is registered for Guid '{typeId}'.");
            }

            return GetGenericArgument(implementationType, openGenericBaseType)
                   ?? throw new JsonException(
                       $"The registered {kind} implementation '{implementationType.FullName}' does not define a profile type.");
        }

        private static Type? GetGenericArgument(Type type, Type openGenericBaseType)
        {
            for (var current = type.BaseType; current is not null; current = current.BaseType)
            {
                if (current.IsGenericType && current.GetGenericTypeDefinition() == openGenericBaseType)
                {
                    return current.GetGenericArguments()[0];
                }
            }

            return null;
        }

        private static void Add<TKey>(
            IDictionary<TKey, Type> dictionary,
            TKey key,
            Type value,
            string kind) where TKey : notnull
        {
            if (dictionary.TryGetValue(key, out var existingValue) && existingValue != value)
            {
                throw new InvalidOperationException(
                    $"Multiple {kind} implementations are registered for the same key: " +
                    $"'{existingValue.FullName}' and '{value.FullName}'.");
            }

            dictionary[key] = value;
        }

        private static void Add<TKey>(
            IDictionary<TKey, Guid> dictionary,
            TKey key,
            Guid value,
            string kind) where TKey : notnull
        {
            if (dictionary.TryGetValue(key, out var existingValue) && existingValue != value)
            {
                throw new InvalidOperationException(
                    $"Multiple {kind} identifiers are registered for the same type: " +
                    $"'{existingValue}' and '{value}'.");
            }

            dictionary[key] = value;
        }
    }
}
