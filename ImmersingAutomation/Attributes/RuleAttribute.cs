namespace ImmersingAutomation.Attributes;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public class RuleAttribute : Attribute
{
    public Guid Guid { get; }
    public string Name { get; }
    
    public RuleAttribute(Guid guid, string name)
    {
        Guid = guid;
        Name = name;
    }
}