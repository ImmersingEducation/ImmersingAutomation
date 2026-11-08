using ImmersingAutomation.Attributes;

namespace ImmersingAutomation.Tests;

public class AttributeTests
{
    [Fact]
    public void TriggerAttribute_GuidConstructor_StoresValues()
    {
        var guid = Guid.NewGuid();

        var attribute = new TriggerAttribute(guid, "trigger");

        Assert.Equal(guid, attribute.Guid);
        Assert.Equal("trigger", attribute.Name);
    }

    [Fact]
    public void TriggerAttribute_StringConstructor_ParsesGuid()
    {
        var guid = Guid.NewGuid();

        var attribute = new TriggerAttribute(guid.ToString("D"), "trigger");

        Assert.Equal(guid, attribute.Guid);
    }

    [Fact]
    public void TriggerAttribute_InvalidGuid_Throws()
    {
        Assert.Throws<FormatException>(() => new TriggerAttribute("not-a-guid", "trigger"));
    }

    [Fact]
    public void RuleAttribute_StoresValues()
    {
        var guid = Guid.NewGuid();

        var attribute = new RuleAttribute(guid, "rule");

        Assert.Equal(guid, attribute.Guid);
        Assert.Equal("rule", attribute.Name);
        Assert.Equal(guid, new RuleAttribute(guid.ToString("D"), "rule").Guid);
    }

    [Fact]
    public void ActionAttribute_StoresReversionSupport()
    {
        var guid = Guid.NewGuid();

        var supported = new ActionAttribute(guid, "action", isReversionSupported: true);
        var unsupported = new ActionAttribute(guid.ToString("D"), "action", isReversionSupported: false);

        Assert.Equal(guid, supported.Guid);
        Assert.Equal("action", supported.Name);
        Assert.True(supported.IsReversionSupported);
        Assert.False(unsupported.IsReversionSupported);
    }

    [Fact]
    public void TriggerProfileAttribute_AcceptsTriggerType()
    {
        var attribute = new TriggerProfileAttribute(typeof(TestTrigger));

        Assert.Equal(typeof(TestTrigger), attribute.TriggerType);
    }

    [Fact]
    public void TriggerProfileAttribute_RejectsInvalidType()
    {
        Assert.Throws<ArgumentNullException>(() => new TriggerProfileAttribute(null!));
        Assert.Throws<ArgumentException>(() => new TriggerProfileAttribute(typeof(TestRule)));
    }

    [Fact]
    public void ActionProfileAttribute_AcceptsActionType()
    {
        var attribute = new ActionProfileAttribute(typeof(TestAction));

        Assert.Equal(typeof(TestAction), attribute.ActionType);
    }

    [Fact]
    public void ActionProfileAttribute_RejectsInvalidType()
    {
        Assert.Throws<ArgumentNullException>(() => new ActionProfileAttribute(null!));
        Assert.Throws<ArgumentException>(() => new ActionProfileAttribute(typeof(TestTrigger)));
    }

    [Fact]
    public void RuleProfileAttribute_AcceptsRuleType()
    {
        var attribute = new RuleProfileAttribute(typeof(TestRule));

        Assert.Equal(typeof(TestRule), attribute.RuleType);
    }

    [Fact]
    public void RuleProfileAttribute_RejectsInvalidType()
    {
        Assert.Throws<ArgumentNullException>(() => new RuleProfileAttribute(null!));
        Assert.Throws<ArgumentException>(() => new RuleProfileAttribute(typeof(TestAction)));
    }
}
