namespace ImmersingAutomation.Abstractions;

public abstract class RuleBase
{
    protected object _profile { get; set; }
    
    public bool IsReversed { get; set; }

    protected abstract Task<bool> _ValidateAsync();

    public async Task<bool> ValidateAsync()
    {
        var result = await _ValidateAsync();
        return IsReversed ? !result : result;
    } 
}

public abstract class RuleBase<T> : RuleBase where T : RuleProfileBase
{
    public T Profile => (_profile as T)!;
}