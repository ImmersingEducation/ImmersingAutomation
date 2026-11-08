using ImmersingAutomation.Abstractions;
using ImmersingAutomation.Enums;
using ImmersingAutomation.Models;

namespace ImmersingAutomation.Tests;

public class RulesetTests
{
    [Fact]
    public async Task AllMode_WhenEveryRuleMatches_ReturnsTrue()
    {
        var first = TestWorkflows.CreateRule(true);
        var second = TestWorkflows.CreateRule(true);
        var ruleset = CreateRuleset(RulesetMode.All, first, second);

        Assert.True(await ruleset.ValidateAsync());
        Assert.Equal(1, first.Profile.ValidateCount);
        Assert.Equal(1, second.Profile.ValidateCount);
    }

    [Fact]
    public async Task AllMode_WhenOneRuleDoesNotMatch_ReturnsFalseAndStops()
    {
        var first = TestWorkflows.CreateRule(false);
        var second = TestWorkflows.CreateRule(true);
        var ruleset = CreateRuleset(RulesetMode.All, first, second);

        Assert.False(await ruleset.ValidateAsync());
        Assert.Equal(1, first.Profile.ValidateCount);
        Assert.Equal(0, second.Profile.ValidateCount);
    }

    [Fact]
    public async Task AnyMode_WhenOneRuleMatches_ReturnsTrueAndStops()
    {
        var first = TestWorkflows.CreateRule(true);
        var second = TestWorkflows.CreateRule(false);
        var ruleset = CreateRuleset(RulesetMode.Any, first, second);

        Assert.True(await ruleset.ValidateAsync());
        Assert.Equal(1, first.Profile.ValidateCount);
        Assert.Equal(0, second.Profile.ValidateCount);
    }

    [Fact]
    public async Task AnyMode_WhenNoRuleMatches_ReturnsFalse()
    {
        var first = TestWorkflows.CreateRule(false);
        var second = TestWorkflows.CreateRule(false);
        var ruleset = CreateRuleset(RulesetMode.Any, first, second);

        Assert.False(await ruleset.ValidateAsync());
        Assert.Equal(1, first.Profile.ValidateCount);
        Assert.Equal(1, second.Profile.ValidateCount);
    }

    [Fact]
    public async Task AllMode_WithNoRules_ReturnsTrue()
    {
        var ruleset = CreateRuleset(RulesetMode.All);

        Assert.True(await ruleset.ValidateAsync());
    }

    [Fact]
    public async Task AnyMode_WithNoRules_ReturnsFalse()
    {
        var ruleset = CreateRuleset(RulesetMode.Any);

        Assert.False(await ruleset.ValidateAsync());
    }

    [Fact]
    public async Task Ruleset_WithNullRules_IsTreatedAsEmpty()
    {
        var ruleset = new Ruleset
        {
            Profile = new RulesetProfile
            {
                Mode = RulesetMode.All,
                Rules = null!
            }
        };

        Assert.True(await ruleset.ValidateAsync());
    }

    [Fact]
    public async Task ReversedRule_InvertsResult()
    {
        var rule = TestWorkflows.CreateRule(false, isReversed: true);

        Assert.True(await rule.ValidateAsync());
    }

    [Fact]
    public async Task ReversedRuleset_InvertsResult()
    {
        var ruleset = CreateRuleset(RulesetMode.All);
        ruleset.IsReversed = true;

        Assert.False(await ruleset.ValidateAsync());
    }

    [Fact]
    public async Task RuleProfile_IsExposed()
    {
        var profile = new TestRuleProfile { Result = true };
        var rule = new TestRule { Profile = profile };

        Assert.True(await rule.ValidateAsync());
        Assert.Same(profile, rule.Profile);
    }

    [Fact]
    public async Task ValidateAsync_WhenCancelled_Throws()
    {
        var ruleset = CreateRuleset(RulesetMode.All, TestWorkflows.CreateRule(true));
        using var cancellationSource = new CancellationTokenSource();
        await cancellationSource.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ruleset.ValidateAsync(cancellationSource.Token));
    }

    private static Ruleset CreateRuleset(RulesetMode mode, params RuleBase[] rules)
    {
        return new Ruleset
        {
            Profile = TestWorkflows.CreateRuleset(mode, rules)
        };
    }
}
