namespace ImmersingAutomation.Attributes;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public class ActionAttribute : Attribute
{
    public Guid Guid { get; }
    public string Name { get; }
    public bool IsReversionSupported { get; }
    
    public ActionAttribute(Guid guid, string name, bool isReversionSupported)
    {
        Guid = guid;
        Name = name;
        IsReversionSupported = isReversionSupported;
    }
}