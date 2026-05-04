using Hermod.Coordinator.Controllers;
using Hermod.Coordinator.UnitTests.TestUtilities;
using Hermod.Core.Interfaces;
using Hermod.Core.Models;
using Hermod.Core.Models.Rules;
using Hermod.TestInfrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Hermod.Coordinator.UnitTests;

/// <summary>
/// Pins <see cref="RulesController"/>'s REST contract plus the key
/// invariant that every write (Create / Update / SetEnabled / Delete)
/// calls <see cref="IRulesEngine.InvalidateCache"/> — forgetting the
/// invalidation would leave the next message dispatch matching the
/// stale rule set, which is how cycle-2's rule-cache regression hid for
/// three days.
/// </summary>
public class RulesControllerTests
{
    [Fact]
    public void RulesController_HasClassLevelAuthorize()
        => ControllerAttributeAsserts.AssertHasClassAuthorize<RulesController>();

    [Fact]
    public void RulesController_NoAllowAnonymousOnEndpoints()
        => ControllerAttributeAsserts.AssertNoAllowAnonymousOnEndpoints<RulesController>();

    [Fact]
    public void RulesController_ExpectedEndpointMethodsArePresent()
        => ControllerAttributeAsserts.AssertEndpointMethodsPresent<RulesController>(
            "GetAll", "GetById", "Create", "Update", "SetEnabled", "Delete");

    private static RulesController Build(
        InMemoryRulesService? rules = null,
        StubRulesEngine? engine = null)
    {
        return new RulesController(
            rules ?? new InMemoryRulesService(),
            engine ?? new StubRulesEngine(),
            NullLogger<RulesController>.Instance);
    }

    private static Rule MakeRule(string id, bool enabled = true) => new()
    {
        Id = id,
        Name = id,
        Enabled = enabled,
        Trigger = new RuleTrigger { TopicPattern = "#", Type = TriggerType.OnMessage },
    };

    [Fact]
    public async Task GetAll_NoFilter_ReturnsEveryRule()
    {
        var rules = new InMemoryRulesService(new[] { MakeRule("a"), MakeRule("b", enabled: false) });
        var sut = Build(rules);

        var result = await sut.GetAll(enabled: null);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var rows = Assert.IsAssignableFrom<IEnumerable<Rule>>(ok.Value).ToList();
        Assert.Equal(2, rows.Count);
    }

    [Theory]
    [InlineData(true, "a")]
    [InlineData(false, "b")]
    public async Task GetAll_FiltersByEnabled(bool enabled, string expectedId)
    {
        var rules = new InMemoryRulesService(new[] { MakeRule("a"), MakeRule("b", enabled: false) });
        var sut = Build(rules);

        var result = await sut.GetAll(enabled: enabled);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var rows = Assert.IsAssignableFrom<IEnumerable<Rule>>(ok.Value).ToList();
        Assert.Single(rows);
        Assert.Equal(expectedId, rows[0].Id);
    }

    [Fact]
    public async Task GetById_Missing_Returns404()
    {
        var sut = Build();

        var result = await sut.GetById("ghost");

        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetById_Present_ReturnsRule()
    {
        var rules = new InMemoryRulesService(new[] { MakeRule("r1") });
        var sut = Build(rules);

        var result = await sut.GetById("r1");

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal("r1", Assert.IsType<Rule>(ok.Value).Id);
    }

    [Fact]
    public async Task Create_EmptyName_ReturnsBadRequest_AndDoesNotInvalidateCache()
    {
        var engine = new StubRulesEngine();
        var sut = Build(engine: engine);

        var result = await sut.Create(new CreateRuleRequest { Name = "" });

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal(0, engine.InvalidateCalls);
    }

    [Fact]
    public async Task Create_ValidRequest_ReturnsCreatedAtAction_AndInvalidatesCache()
    {
        var rules = new InMemoryRulesService();
        var engine = new StubRulesEngine();
        var sut = Build(rules, engine);

        var result = await sut.Create(new CreateRuleRequest
        {
            Name = "new-rule",
            Description = "test",
        });

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var rule = Assert.IsType<Rule>(created.Value);
        Assert.Equal("new-rule", rule.Name);
        Assert.True(rules.Rules.ContainsKey(rule.Id));
        Assert.Equal(1, engine.InvalidateCalls);
    }

    [Fact]
    public async Task Create_NoTrigger_DefaultsToOnMessageWildcard()
    {
        var rules = new InMemoryRulesService();
        var sut = Build(rules);

        var result = await sut.Create(new CreateRuleRequest { Name = "default-trigger" });

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var rule = Assert.IsType<Rule>(created.Value);
        Assert.Equal("#", rule.Trigger.TopicPattern);
        Assert.Equal(TriggerType.OnMessage, rule.Trigger.Type);
    }

    [Fact]
    public async Task Update_MissingRule_Returns404_AndDoesNotInvalidateCache()
    {
        var engine = new StubRulesEngine();
        var sut = Build(engine: engine);

        var result = await sut.Update("ghost", new UpdateRuleRequest { Name = "irrelevant" });

        Assert.IsType<NotFoundObjectResult>(result.Result);
        Assert.Equal(0, engine.InvalidateCalls);
    }

    [Fact]
    public async Task Update_NullFields_PreserveCurrentValues()
    {
        // Regression guard: PUT body with only {enabled: false} must leave
        // Name/Description/Trigger/Conditions/Actions untouched.
        var original = MakeRule("r1");
        original.Description = "orig-desc";
        original.Enabled = true;
        var rules = new InMemoryRulesService(new[] { original });
        var engine = new StubRulesEngine();
        var sut = Build(rules, engine);

        var result = await sut.Update("r1", new UpdateRuleRequest { Enabled = false });

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var updated = Assert.IsType<Rule>(ok.Value);
        Assert.Equal("r1", updated.Name);
        Assert.Equal("orig-desc", updated.Description);
        Assert.False(updated.Enabled);
        Assert.Equal(1, engine.InvalidateCalls);
    }

    [Fact]
    public async Task Update_OverwritesOnlyProvidedFields()
    {
        var original = MakeRule("r1");
        original.Description = "orig";
        var rules = new InMemoryRulesService(new[] { original });
        var sut = Build(rules);

        var result = await sut.Update("r1", new UpdateRuleRequest
        {
            Description = "new-desc",
            Actions = new List<RuleAction> { new() { Type = ActionType.Log, LogMessage = "hi" } },
        });

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var updated = Assert.IsType<Rule>(ok.Value);
        Assert.Equal("r1", updated.Name);
        Assert.Equal("new-desc", updated.Description);
        Assert.Single(updated.Actions);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SetEnabled_Present_TogglesStateAndInvalidatesCache(bool targetState)
    {
        var rules = new InMemoryRulesService(new[] { MakeRule("r1", enabled: !targetState) });
        var engine = new StubRulesEngine();
        var sut = Build(rules, engine);

        var result = await sut.SetEnabled("r1", new EnableRuleRequest { Enabled = targetState });

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(targetState, rules.Rules["r1"].Enabled);
        Assert.Equal(1, engine.InvalidateCalls);
    }

    [Fact]
    public async Task SetEnabled_Missing_Returns404_AndDoesNotInvalidateCache()
    {
        var engine = new StubRulesEngine();
        var sut = Build(engine: engine);

        var result = await sut.SetEnabled("ghost", new EnableRuleRequest { Enabled = true });

        Assert.IsType<NotFoundObjectResult>(result);
        Assert.Equal(0, engine.InvalidateCalls);
    }

    [Fact]
    public async Task Delete_Missing_Returns404_AndDoesNotInvalidateCache()
    {
        var engine = new StubRulesEngine();
        var sut = Build(engine: engine);

        var result = await sut.Delete("ghost");

        Assert.IsType<NotFoundObjectResult>(result);
        Assert.Equal(0, engine.InvalidateCalls);
    }

    [Fact]
    public async Task Delete_Present_RemovesAndInvalidatesCache()
    {
        var rules = new InMemoryRulesService(new[] { MakeRule("r1") });
        var engine = new StubRulesEngine();
        var sut = Build(rules, engine);

        var result = await sut.Delete("r1");

        Assert.IsType<OkObjectResult>(result);
        Assert.False(rules.Rules.ContainsKey("r1"));
        Assert.Equal(1, engine.InvalidateCalls);
    }

    private sealed class StubRulesEngine : IRulesEngine
    {
        public int InvalidateCalls;
        public void InvalidateCache() => Interlocked.Increment(ref InvalidateCalls);

        // Stub for the enforcement gate at RulesController.cs:88; tests
        // that don't exercise the limit can leave this at 0.
        public int TotalRuleCount { get; set; }

#pragma warning disable CS0067
        public event EventHandler<RuleExecutedEventArgs>? RuleExecuted;
        public event EventHandler<RuleErrorEventArgs>? RuleError;
#pragma warning restore CS0067

        private static Task Fail() => throw new NotSupportedException("test stub: not part of the exercised path");
        private static T Fail<T>() => throw new NotSupportedException("test stub: not part of the exercised path");

        public Task ProcessMessageAsync(ProcessedMessage message, CancellationToken cancellationToken = default) => Fail();
        public Task ProcessMessageAsync(MqttMessage message, CancellationToken cancellationToken = default) => Fail();
        public Task TriggerRuleAsync(string ruleId, Dictionary<string, object>? chainData = null, ProcessedMessage? sourceMessage = null, CancellationToken cancellationToken = default) => Fail();
        public string ScheduleRuleTrigger(string ruleId, TimeSpan delay, Dictionary<string, object>? chainData = null) => Fail<string>();
        public bool CancelScheduledTrigger(string scheduleId) => Fail<bool>();
        public Task ExecuteStartupRulesAsync(CancellationToken cancellationToken = default) => Fail();
        public T? GetGlobalState<T>(string key) => default;
        public void SetGlobalState(string key, object value) => throw new NotSupportedException("test stub");
    }
}
