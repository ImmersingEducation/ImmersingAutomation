using System.Reflection;
using ImmersingAutomation.Abstractions;
using ImmersingAutomation.Attributes;
using Microsoft.Extensions.DependencyInjection;

namespace ImmersingAutomation;

/// <summary>
/// 保存自动化类型与其持久化标识符之间的映射。
/// </summary>
/// <remarks>
/// 注册表在创建后不可变，因此可以由多个工作流序列化和反序列化操作共享。
/// 应在注册所有自动化类型之后创建注册表。
/// </remarks>
public sealed class AutomationTypeRegistry
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

    private AutomationTypeRegistry()
    {
    }

    /// <summary>
    /// 从服务集合中的注册项创建注册表。
    /// </summary>
    /// <param name="services">包含自动化类型注册项的服务集合。</param>
    /// <returns>不可变的类型注册表。</returns>
    public static AutomationTypeRegistry Create(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var registry = new AutomationTypeRegistry();
        var implementationTypes = new HashSet<Type>();

        foreach (var descriptor in services)
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

    /// <summary>
    /// 从显式的类型集合创建注册表。
    /// </summary>
    /// <param name="types">要注册的自动化类型。</param>
    /// <returns>不可变的类型注册表。</returns>
    public static AutomationTypeRegistry Create(IEnumerable<Type> types)
    {
        ArgumentNullException.ThrowIfNull(types);

        var registry = new AutomationTypeRegistry();

        foreach (var type in types.Distinct())
        {
            if (type.IsAbstract || type.IsInterface || type.IsGenericTypeDefinition)
            {
                continue;
            }

            registry.Register(type);
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

    /// <summary>
    /// 获取具体类型的持久化标识符。
    /// </summary>
    /// <typeparam name="TBase">多态基类型。</typeparam>
    /// <param name="concreteType">具体类型。</param>
    /// <returns>与具体类型关联的标识符。</returns>
    public Guid GetTypeId<TBase>(Type concreteType) where TBase : class
    {
        ArgumentNullException.ThrowIfNull(concreteType);

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

    /// <summary>
    /// 获取标识符对应的具体类型。
    /// </summary>
    /// <typeparam name="TBase">多态基类型。</typeparam>
    /// <param name="typeId">持久化标识符。</param>
    /// <returns>具体类型。</returns>
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

            throw new System.Text.Json.JsonException(
                $"No rule implementation is registered for Guid '{typeId}'.");
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
            throw new System.Text.Json.JsonException(
                $"No {kind} implementation is registered for Guid '{typeId}'.");
        }

        return GetGenericArgument(implementationType, openGenericBaseType)
               ?? throw new System.Text.Json.JsonException(
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
