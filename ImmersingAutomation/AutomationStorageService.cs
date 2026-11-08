using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ImmersingAutomation.Abstractions;
using ImmersingAutomation.Attributes;
using ImmersingAutomation.Models;
using Microsoft.Extensions.DependencyInjection;

namespace ImmersingAutomation;

public sealed class AutomationStorageService : IAutomationStorageService
{
    private const string WorkflowFileExtension = ".json";
    private const string TypePropertyName = "$type";

    private readonly AutomationService _automationService;

    public AutomationStorageService(AutomationService automationService, string storagePath)
    {
        ArgumentNullException.ThrowIfNull(automationService);
        ArgumentException.ThrowIfNullOrWhiteSpace(storagePath);

        _automationService = automationService;
        StoragePath = Path.GetFullPath(storagePath);
    }

    public string StoragePath { get; }

    public string SerializeWorkflow(Workflow workflow)
    {
        ArgumentNullException.ThrowIfNull(workflow);

        return SerializeWorkflow(workflow, CreateJsonSerializerOptions());
    }

    public Workflow DeserializeWorkflow(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        return DeserializeWorkflow(json, CreateJsonSerializerOptions());
    }

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

    public bool WorkflowExists(Guid workflowId)
    {
        return File.Exists(GetWorkflowFilePath(workflowId));
    }

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
        private readonly Dictionary<Type, Guid> _ruleTypeIds = new();

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

        public Guid GetTypeId<TBase>(Type concreteType) where TBase : class
        {
            var baseType = typeof(TBase);

            if (baseType == typeof(TriggerProfileBase))
            {
                if (_triggerProfileTypeIds.TryGetValue(concreteType, out var triggerTypeId))
                {
                    return triggerTypeId;
                }

                var triggerAttribute = concreteType.GetCustomAttribute<TriggerAttribute>(inherit: false);
                if (triggerAttribute is not null)
                {
                    return triggerAttribute.Guid;
                }

                throw new InvalidOperationException(
                    $"Trigger profile type '{concreteType.FullName}' is not associated with a registered trigger. " +
                    $"Make sure the corresponding trigger implementation is registered and has a [{nameof(TriggerAttribute)}].");
            }

            if (baseType == typeof(ActionProfileBase))
            {
                if (_actionProfileTypeIds.TryGetValue(concreteType, out var actionTypeId))
                {
                    return actionTypeId;
                }

                var actionAttribute = concreteType.GetCustomAttribute<ActionAttribute>(inherit: false);
                if (actionAttribute is not null)
                {
                    return actionAttribute.Guid;
                }

                throw new InvalidOperationException(
                    $"Action profile type '{concreteType.FullName}' is not associated with a registered action. " +
                    $"Make sure the corresponding action implementation is registered and has a [{nameof(ActionAttribute)}].");
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
                return GetProfileType(_triggerTypes, typeId, typeof(TriggerBase<>), "trigger");
            }

            if (baseType == typeof(ActionProfileBase))
            {
                return GetProfileType(_actionTypes, typeId, typeof(ActionBase<>), "action");
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
