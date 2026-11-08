namespace ImmersingAutomation.Attributes;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public class TriggerAttribute : Attribute
{
    public Guid Guid { get; }
    public string Name { get; }
    
    
    public TriggerAttribute(Guid guid, string name)
    {
        Guid = guid;
        Name = name;
    }
}