namespace ImmersingAutomation.Abstractions;

public abstract class TriggerBase
{
    protected object _profile { get; set; }

    public event EventHandler? Triggered;

    protected void Trigger()
    {
        Triggered?.Invoke(this, EventArgs.Empty);
    }

    public abstract Task Initialize();

    public abstract Task Release();
}

public abstract class TriggerBase<T> : TriggerBase where T : class
{
    public T Profile => (_profile as T)!;
}