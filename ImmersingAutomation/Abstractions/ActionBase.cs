namespace ImmersingAutomation.Abstractions;

public abstract class ActionBase
{
    protected object _profile;

    private readonly CancellationTokenSource _interruptionSource = new();

    protected CancellationToken InterruptionToken => _interruptionSource.Token;

    public void Interrupt()
    {
        _interruptionSource.Cancel();
    }

    protected void ThrowIfInterrupted()
    {
        InterruptionToken.ThrowIfCancellationRequested();
    }

    public abstract Task RunAsync();

    public abstract Task RevertAsync();
}

public abstract class ActionBase<T> : ActionBase where T : class
{
    public T Profile => (_profile as T)!;
}