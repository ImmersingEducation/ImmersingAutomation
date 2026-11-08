namespace ImmersingAutomation.Abstractions;

/// <summary>
/// 可验证并可选择取反的条件的基类。
/// </summary>
public abstract class RuleBase
{
    /// <summary>
    /// 获取或设置由泛型派生类提供的配置对象。
    /// </summary>
    protected object _profile { get; set; }

    /// <summary>
    /// 获取或设置是否对验证结果取反。
    /// </summary>
    public bool IsReversed { get; set; }

    /// <summary>
    /// 评估规则，不应用 <see cref="IsReversed"/>。
    /// </summary>
    protected abstract Task<bool> _ValidateAsync();

    /// <summary>
    /// 评估规则，并对结果应用 <see cref="IsReversed"/>。
    /// </summary>
    /// <returns>规则匹配时返回 <see langword="true"/>。</returns>
    public async Task<bool> ValidateAsync()
    {
        var result = await _ValidateAsync();
        return IsReversed ? !result : result;
    } 
}

/// <summary>
/// 使用强类型配置的规则的基类。
/// </summary>
/// <typeparam name="T">规则的配置类型。</typeparam>
public abstract class RuleBase<T> : RuleBase where T : RuleProfileBase
{
    /// <summary>
    /// 获取或设置规则的配置。
    /// </summary>
    public T Profile
    {
        get => (_profile as T)!;
        init => _profile = value;
    }
}
