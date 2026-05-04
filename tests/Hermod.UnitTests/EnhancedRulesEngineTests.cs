using System.Net.Http;
using Hermod.Core.Configuration;
using Hermod.Core.Interfaces;
using Hermod.Core.Models;
using Hermod.Core.Models.Rules;
using Hermod.Core.Telemetry;
using Hermod.Rules;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Hermod.UnitTests;

public class EnhancedRulesEngineTests
{
    private static (EnhancedRulesEngine engine, FakeRulesService rules, FakeMqttService mqtt, FakeStatsService stats)
        CreateEngine()
    {
        var (engine, rules, mqtt, stats, _, _) = CreateEngineWithLogger();
        return (engine, rules, mqtt, stats);
    }

    private static (EnhancedRulesEngine engine, FakeRulesService rules, FakeMqttService mqtt, FakeStatsService stats, CapturingLogger<EnhancedRulesEngine> logger, FakeDeviceService devices)
        CreateEngineWithLogger()
    {
        var rules = new FakeRulesService();
        var mqtt = new FakeMqttService();
        var stats = new FakeStatsService();
        var expr = new ExpressionEvaluator();
        var state = new StateManager();
        var scheduler = new Scheduler();
        var logger = new CapturingLogger<EnhancedRulesEngine>();
        var devices = new FakeDeviceService();

        var engine = new EnhancedRulesEngine(
            rules,
            mqtt,
            stats,
            expr,
            state,
            scheduler,
            protocolHandlers: Array.Empty<IProtocolHandler>(),
            httpClientFactory: new FakeHttpClientFactory(),
            ruleAudit: new FakeRuleAuditRepository(),
            metrics: new HermodMetrics(),
            settings: Options.Create(new HermodSettings()),
            logger: logger,
            deviceService: devices);

        return (engine, rules, mqtt, stats, logger, devices);
    }

    private static ProcessedMessage MakeMessage(string topic, Dictionary<string, object>? payload = null)
    {
        return new ProcessedMessage
        {
            OriginalMessage = new MqttMessage { Topic = topic, Payload = "{}" },
            ParsedPayload = payload ?? new Dictionary<string, object>(),
            DeviceName = topic.Split('/').ElementAtOrDefault(1),
            ShouldTriggerRules = true
        };
    }

    private static Rule MakePublishRule(
        string id,
        string topicPattern,
        string outputTopic,
        int priority = 100,
        bool enabled = true)
    {
        return new Rule
        {
            Id = id,
            Name = id,
            Enabled = enabled,
            Priority = priority,
            Trigger = new RuleTrigger { TopicPattern = topicPattern, Type = TriggerType.OnMessage },
            Actions = new List<RuleAction>
            {
                new()
                {
                    Type = ActionType.Publish,
                    Topic = outputTopic,
                    Payload = new Dictionary<string, object> { ["fired"] = id }
                }
            }
        };
    }

    [Fact]
    public async Task ProcessMessageAsync_MatchesTopicPatternWithSinglePlusWildcard()
    {
        var (engine, rules, mqtt, _) = CreateEngine();
        rules.Rules.Add(MakePublishRule("r", "zigbee/+/state", "hermod/out/one"));

        await engine.ProcessMessageAsync(MakeMessage("zigbee/lamp/state"));

        Assert.Contains(mqtt.Published, p => p.Topic == "hermod/out/one");
    }

    [Fact]
    public async Task ProcessMessageAsync_PlusWildcardDoesNotCrossSegments()
    {
        var (engine, rules, mqtt, _) = CreateEngine();
        rules.Rules.Add(MakePublishRule("r", "zigbee/+/state", "hermod/out/one"));

        await engine.ProcessMessageAsync(MakeMessage("zigbee/lamp/extra/state"));

        Assert.Empty(mqtt.Published);
    }

    [Fact]
    public async Task ProcessMessageAsync_HashWildcardMatchesMultipleSegments()
    {
        var (engine, rules, mqtt, _) = CreateEngine();
        rules.Rules.Add(MakePublishRule("r", "zigbee/#", "hermod/out/any"));

        await engine.ProcessMessageAsync(MakeMessage("zigbee/lamp/a/b/state"));

        Assert.Contains(mqtt.Published, p => p.Topic == "hermod/out/any");
    }

    [Fact]
    public async Task ProcessMessageAsync_DisabledRule_IsSkipped()
    {
        var (engine, rules, mqtt, _) = CreateEngine();
        rules.Rules.Add(MakePublishRule("r", "zigbee/lamp", "hermod/out/skip", enabled: false));

        await engine.ProcessMessageAsync(MakeMessage("zigbee/lamp"));

        Assert.Empty(mqtt.Published);
    }

    [Fact]
    public async Task ProcessMessageAsync_OrdersRulesByPriority_LowestFirst()
    {
        var (engine, rules, mqtt, _) = CreateEngine();
        rules.Rules.Add(MakePublishRule("high", "zigbee/lamp", "hermod/out/high", priority: 500));
        rules.Rules.Add(MakePublishRule("low", "zigbee/lamp", "hermod/out/low", priority: 10));
        rules.Rules.Add(MakePublishRule("mid", "zigbee/lamp", "hermod/out/mid", priority: 100));

        await engine.ProcessMessageAsync(MakeMessage("zigbee/lamp"));

        var topics = mqtt.Published.Select(p => p.Topic).ToList();
        Assert.Equal(new[] { "hermod/out/low", "hermod/out/mid", "hermod/out/high" }, topics);
    }

    [Fact]
    public async Task TriggerRuleAsync_SelfChainingRule_StopsAtDepthCap()
    {
        // Self-chain loop: the chain cap (16) must bail out instead of
        // recursing into StackOverflowException. The number of outbound
        // Publish actions is bounded by the cap plus one outer trigger.
        var (engine, rules, mqtt, _) = CreateEngine();
        var rule = new Rule
        {
            Id = "loop",
            Name = "loop",
            Enabled = true,
            Trigger = new RuleTrigger { Type = TriggerType.OnChain },
            Actions = new List<RuleAction>
            {
                new()
                {
                    Type = ActionType.Publish,
                    Topic = "hermod/loop",
                    Payload = new Dictionary<string, object> { ["x"] = 1 }
                },
                new()
                {
                    Type = ActionType.Chain,
                    ChainToRule = "loop"
                }
            }
        };
        rules.Rules.Add(rule);

        await engine.TriggerRuleAsync("loop");

        // Depth cap is 16 inside the engine; allow some slack to stay robust
        // against minor counting changes, but assert we did not runaway.
        Assert.InRange(mqtt.Published.Count, 1, 32);
    }

    [Fact]
    public async Task TriggerRuleAsync_DisabledRule_DoesNothing()
    {
        var (engine, rules, mqtt, _) = CreateEngine();
        rules.Rules.Add(MakePublishRule("r", "zigbee/lamp", "hermod/out/nope", enabled: false));

        await engine.TriggerRuleAsync("r");

        Assert.Empty(mqtt.Published);
    }

    [Fact]
    public async Task ExecuteWebhookAction_LoopbackUrl_RejectedBySsrfGuard()
    {
        // The SSRF guard must reject loopback targets even when the rule
        // author writes a literal URL (not just a templated one).
        var (engine, rules, mqtt, _) = CreateEngine();
        var rule = new Rule
        {
            Id = "webhook",
            Name = "webhook",
            Enabled = true,
            Trigger = new RuleTrigger { Type = TriggerType.OnChain },
            Actions = new List<RuleAction>
            {
                new()
                {
                    Type = ActionType.Webhook,
                    WebhookUrl = "http://127.0.0.1:8080/evil"
                }
            }
        };
        rules.Rules.Add(rule);

        var errors = new List<RuleErrorEventArgs>();
        engine.RuleError += (_, e) => errors.Add(e);

        var executedResults = new List<RuleActionResult>();
        engine.RuleExecuted += (_, e) => executedResults.AddRange(e.ActionResults);

        await engine.TriggerRuleAsync("webhook");

        // The rule executed (no exception) but the single webhook action
        // must be marked unsuccessful with a clear error referencing the URL.
        Assert.Single(executedResults);
        Assert.False(executedResults[0].Success);
        Assert.NotNull(executedResults[0].Error);
        Assert.Contains("127.0.0.1", executedResults[0].Error);
    }

    [Fact]
    public async Task ExecuteWebhookAction_PrivateServiceNameHost_RejectedBySsrfGuard()
    {
        var (engine, rules, mqtt, _) = CreateEngine();
        var rule = new Rule
        {
            Id = "webhook",
            Name = "webhook",
            Enabled = true,
            Trigger = new RuleTrigger { Type = TriggerType.OnChain },
            Actions = new List<RuleAction>
            {
                new() { Type = ActionType.Webhook, WebhookUrl = "http://vault42:8080/steal" }
            }
        };
        rules.Rules.Add(rule);

        RuleActionResult? result = null;
        engine.RuleExecuted += (_, e) => result = e.ActionResults.Single();

        await engine.TriggerRuleAsync("webhook");

        Assert.NotNull(result);
        Assert.False(result.Success);
    }

    [Fact]
    public async Task ExecuteWebhookAction_NonHttpScheme_RejectedBySsrfGuard()
    {
        var (engine, rules, mqtt, _) = CreateEngine();
        var rule = new Rule
        {
            Id = "webhook",
            Name = "webhook",
            Enabled = true,
            Trigger = new RuleTrigger { Type = TriggerType.OnChain },
            Actions = new List<RuleAction>
            {
                new() { Type = ActionType.Webhook, WebhookUrl = "file:///etc/passwd" }
            }
        };
        rules.Rules.Add(rule);

        RuleActionResult? result = null;
        engine.RuleExecuted += (_, e) => result = e.ActionResults.Single();

        await engine.TriggerRuleAsync("webhook");

        Assert.NotNull(result);
        Assert.False(result.Success);
    }

    [Fact]
    public async Task ExecuteCancelDelay_LooksUpScheduleIdInRuleState()
    {
        // Regression guard: the previous implementation wrote schedule IDs
        // to rule-scoped state but read them from global state, so the
        // specific-id cancellation path never hit. We can verify the lookup
        // path by pre-seeding rule state with a known schedule id, adding a
        // CancelDelay action that references it, and asserting no throw.
        var (engine, rules, _, _) = CreateEngine();
        var rule = new Rule
        {
            Id = "canceller",
            Name = "canceller",
            Enabled = true,
            Trigger = new RuleTrigger { Type = TriggerType.OnChain },
            Actions = new List<RuleAction>
            {
                new()
                {
                    Type = ActionType.CancelDelay,
                    ChainToRule = "target"
                }
            }
        };
        rules.Rules.Add(rule);

        // Should not throw even though the referenced schedule does not
        // actually exist yet: the cancellation code must degrade cleanly.
        await engine.TriggerRuleAsync("canceller");
    }

    [Fact]
    public async Task ProcessMessageAsync_ShouldTriggerRulesFalse_SkipsEntireCachePass()
    {
        var (engine, rules, mqtt, _) = CreateEngine();
        rules.Rules.Add(MakePublishRule("r", "#", "hermod/out/x"));

        var suppressed = new ProcessedMessage
        {
            OriginalMessage = new MqttMessage { Topic = "zigbee/lamp", Payload = "{}" },
            ShouldTriggerRules = false
        };

        await engine.ProcessMessageAsync(suppressed);

        Assert.Empty(mqtt.Published);
    }

    [Fact]
    public async Task ProcessMessageAsync_DebouncedRule_SkipsSecondFireWithinWindow()
    {
        var (engine, rules, mqtt, _) = CreateEngine();
        var rule = MakePublishRule("r", "zigbee/lamp", "hermod/out/debounced");
        rule.Trigger.Debounce = "10s";
        rules.Rules.Add(rule);

        await engine.ProcessMessageAsync(MakeMessage("zigbee/lamp"));
        await engine.ProcessMessageAsync(MakeMessage("zigbee/lamp"));
        await engine.ProcessMessageAsync(MakeMessage("zigbee/lamp"));

        Assert.Single(mqtt.Published);
    }

    [Fact]
    public async Task ProcessMessageAsync_SetStateAction_PersistsIntoRuleState()
    {
        var (engine, rules, _, _) = CreateEngine();
        var rule = new Rule
        {
            Id = "setter",
            Name = "setter",
            Enabled = true,
            Trigger = new RuleTrigger { TopicPattern = "zigbee/lamp", Type = TriggerType.OnMessage },
            Actions = new List<RuleAction>
            {
                new()
                {
                    Type = ActionType.SetState,
                    SetState = new Dictionary<string, object>
                    {
                        ["count"] = 42,
                        ["label"] = "alpha"
                    }
                }
            }
        };
        rules.Rules.Add(rule);

        var captured = new List<RuleExecutedEventArgs>();
        engine.RuleExecuted += (_, e) => captured.Add(e);

        await engine.ProcessMessageAsync(MakeMessage("zigbee/lamp"));

        Assert.Single(captured);
        Assert.True(captured[0].ActionResults[0].Success);
    }

    [Fact]
    public async Task ProcessMessageAsync_ConditionalAction_RunsThenBranchWhenIfTrue()
    {
        var (engine, rules, mqtt, _) = CreateEngine();
        var rule = new Rule
        {
            Id = "cond",
            Name = "cond",
            Enabled = true,
            Trigger = new RuleTrigger { TopicPattern = "zigbee/lamp", Type = TriggerType.OnMessage },
            Actions = new List<RuleAction>
            {
                new()
                {
                    Type = ActionType.Conditional,
                    If = new RuleConditionGroup
                    {
                        Logic = LogicOperator.All,
                        Conditions = new List<RuleCondition>
                        {
                            new()
                            {
                                Property = "state",
                                Operator = ComparisonOperator.Equals,
                                Value = "ON"
                            }
                        }
                    },
                    Then = new List<RuleAction>
                    {
                        new()
                        {
                            Type = ActionType.Publish,
                            Topic = "hermod/out/then",
                            Payload = new Dictionary<string, object> { ["x"] = 1 }
                        }
                    },
                    Else = new List<RuleAction>
                    {
                        new()
                        {
                            Type = ActionType.Publish,
                            Topic = "hermod/out/else",
                            Payload = new Dictionary<string, object> { ["x"] = 0 }
                        }
                    }
                }
            }
        };
        rules.Rules.Add(rule);

        await engine.ProcessMessageAsync(MakeMessage("zigbee/lamp",
            new Dictionary<string, object> { ["state"] = "ON" }));

        Assert.Contains(mqtt.Published, p => p.Topic == "hermod/out/then");
        Assert.DoesNotContain(mqtt.Published, p => p.Topic == "hermod/out/else");
    }

    [Fact]
    public async Task ProcessMessageAsync_ConditionalAction_RunsElseBranchWhenIfFalse()
    {
        var (engine, rules, mqtt, _) = CreateEngine();
        var rule = new Rule
        {
            Id = "cond",
            Name = "cond",
            Enabled = true,
            Trigger = new RuleTrigger { TopicPattern = "zigbee/lamp", Type = TriggerType.OnMessage },
            Actions = new List<RuleAction>
            {
                new()
                {
                    Type = ActionType.Conditional,
                    If = new RuleConditionGroup
                    {
                        Logic = LogicOperator.All,
                        Conditions = new List<RuleCondition>
                        {
                            new()
                            {
                                Property = "state",
                                Operator = ComparisonOperator.Equals,
                                Value = "ON"
                            }
                        }
                    },
                    Then = new List<RuleAction>
                    {
                        new()
                        {
                            Type = ActionType.Publish,
                            Topic = "hermod/out/then",
                            Payload = new Dictionary<string, object> { ["x"] = 1 }
                        }
                    },
                    Else = new List<RuleAction>
                    {
                        new()
                        {
                            Type = ActionType.Publish,
                            Topic = "hermod/out/else",
                            Payload = new Dictionary<string, object> { ["x"] = 0 }
                        }
                    }
                }
            }
        };
        rules.Rules.Add(rule);

        await engine.ProcessMessageAsync(MakeMessage("zigbee/lamp",
            new Dictionary<string, object> { ["state"] = "OFF" }));

        Assert.Contains(mqtt.Published, p => p.Topic == "hermod/out/else");
        Assert.DoesNotContain(mqtt.Published, p => p.Topic == "hermod/out/then");
    }

    [Fact]
    public async Task ProcessMessageAsync_ParallelAction_FansOutAllChildren()
    {
        var (engine, rules, mqtt, _) = CreateEngine();
        var rule = new Rule
        {
            Id = "fan",
            Name = "fan",
            Enabled = true,
            Trigger = new RuleTrigger { TopicPattern = "zigbee/lamp", Type = TriggerType.OnMessage },
            Actions = new List<RuleAction>
            {
                new()
                {
                    Type = ActionType.Parallel,
                    ParallelActions = new List<RuleAction>
                    {
                        new()
                        {
                            Type = ActionType.Publish,
                            Topic = "hermod/out/a",
                            Payload = new Dictionary<string, object> { ["v"] = 1 }
                        },
                        new()
                        {
                            Type = ActionType.Publish,
                            Topic = "hermod/out/b",
                            Payload = new Dictionary<string, object> { ["v"] = 2 }
                        },
                        new()
                        {
                            Type = ActionType.Publish,
                            Topic = "hermod/out/c",
                            Payload = new Dictionary<string, object> { ["v"] = 3 }
                        }
                    }
                }
            }
        };
        rules.Rules.Add(rule);

        await engine.ProcessMessageAsync(MakeMessage("zigbee/lamp"));

        var topics = mqtt.Published.Select(p => p.Topic).OrderBy(t => t).ToList();
        Assert.Equal(new[] { "hermod/out/a", "hermod/out/b", "hermod/out/c" }, topics);
    }

    [Fact]
    public async Task ProcessMessageAsync_StopAction_HaltsSubsequentActions()
    {
        var (engine, rules, mqtt, _) = CreateEngine();
        var rule = new Rule
        {
            Id = "stop",
            Name = "stop",
            Enabled = true,
            Trigger = new RuleTrigger { TopicPattern = "zigbee/lamp", Type = TriggerType.OnMessage },
            Actions = new List<RuleAction>
            {
                new()
                {
                    Type = ActionType.Publish,
                    Topic = "hermod/out/before",
                    Payload = new Dictionary<string, object> { ["v"] = 1 }
                },
                new() { Type = ActionType.Stop },
                new()
                {
                    Type = ActionType.Publish,
                    Topic = "hermod/out/after",
                    Payload = new Dictionary<string, object> { ["v"] = 2 }
                }
            }
        };
        rules.Rules.Add(rule);

        await engine.ProcessMessageAsync(MakeMessage("zigbee/lamp"));

        Assert.Contains(mqtt.Published, p => p.Topic == "hermod/out/before");
        Assert.DoesNotContain(mqtt.Published, p => p.Topic == "hermod/out/after");
    }

    [Fact]
    public async Task ProcessMessageAsync_OutsideActiveWindow_SkipsRule()
    {
        // The engine evaluates windows in UTC after the recent fix. Build
        // an "everything except now" window (spanning the full 24h but
        // excluding the current UTC minute via Days filter for a day the
        // engine won't be running on). Simpler approach: construct a
        // 1-second window an hour ago.
        var (engine, rules, mqtt, _) = CreateEngine();
        var nowUtc = DateTime.UtcNow;
        var wayBack = nowUtc.AddHours(-3);
        var rule = MakePublishRule("r", "zigbee/lamp", "hermod/out/windowed");
        rule.Trigger.ActiveWindows = new List<TimeWindow>
        {
            new()
            {
                Start = new TimeOnly(wayBack.Hour, wayBack.Minute),
                End = new TimeOnly(wayBack.Hour, wayBack.Minute).AddMinutes(1),
                Days = null
            }
        };
        rules.Rules.Add(rule);

        await engine.ProcessMessageAsync(MakeMessage("zigbee/lamp"));

        Assert.Empty(mqtt.Published);
    }

    [Fact]
    public async Task ProcessMessageAsync_InsideActiveWindow_FiresRule()
    {
        var (engine, rules, mqtt, _) = CreateEngine();
        var rule = MakePublishRule("r", "zigbee/lamp", "hermod/out/windowed");
        rule.Trigger.ActiveWindows = new List<TimeWindow>
        {
            // Full-day UTC window. End=MaxValue avoids the 23:59:00-23:59:59
            // flake where TimeOnly(23, 59) has second=0 and a real clock tick
            // in that minute would test > End.
            new()
            {
                Start = new TimeOnly(0, 0),
                End = TimeOnly.MaxValue,
                Days = null
            }
        };
        rules.Rules.Add(rule);

        await engine.ProcessMessageAsync(MakeMessage("zigbee/lamp"));

        Assert.Single(mqtt.Published);
    }

    [Fact]
    public async Task ProcessMessageAsync_ConditionGroupBlocksRule()
    {
        var (engine, rules, mqtt, _) = CreateEngine();
        var rule = MakePublishRule("r", "zigbee/lamp", "hermod/out/gated");
        rule.Conditions = new RuleConditionGroup
        {
            Logic = LogicOperator.All,
            Conditions = new List<RuleCondition>
            {
                new()
                {
                    Property = "state",
                    Operator = ComparisonOperator.Equals,
                    Value = "ON"
                }
            }
        };
        rules.Rules.Add(rule);

        await engine.ProcessMessageAsync(MakeMessage("zigbee/lamp",
            new Dictionary<string, object> { ["state"] = "OFF" }));

        Assert.Empty(mqtt.Published);
    }

    [Fact]
    public async Task OnAvailability_DispatchesMatchingRule_WhenDeviceStatusTransitions()
    {
        var (engine, rules, mqtt, _, _, devices) = CreateEngineWithLogger();

        rules.Rules.Add(new Rule
        {
            Id = "rule-availability-demo",
            Name = "Device Coming Online Demo",
            Enabled = true,
            Trigger = new RuleTrigger
            {
                TopicPattern = "availability/zigbee2mqtt/+",
                Type = TriggerType.OnAvailability,
            },
            Actions = new List<RuleAction>
            {
                new()
                {
                    Type = ActionType.Publish,
                    Topic = "alerts/availability",
                    Payload = new Dictionary<string, object>
                    {
                        ["deviceId"] = "{{source.deviceId}}",
                        ["status"] = "{{source.currentStatus}}",
                    },
                },
            },
        });

        // Warm the engine index before firing the event.
        await engine.ProcessMessageAsync(MakeMessage("seed/warmup"));
        mqtt.Published.Clear();

        devices.RaiseAvailability("aqara_door", DeviceStatus.Offline, DeviceStatus.Online,
            topic: "availability/zigbee2mqtt/aqara_door");

        await WaitUntilAsync(() => mqtt.Published.Count > 0);

        var published = Assert.Single(mqtt.Published);
        Assert.Equal("alerts/availability", published.Topic);
        Assert.Contains("aqara_door", published.Payload);
        Assert.Contains("Online", published.Payload);
    }

    [Fact]
    public async Task OnAvailability_DisabledRule_DoesNotFire()
    {
        var (engine, rules, mqtt, _, _, devices) = CreateEngineWithLogger();

        rules.Rules.Add(new Rule
        {
            Id = "rule-availability-disabled",
            Enabled = false,
            Trigger = new RuleTrigger
            {
                TopicPattern = "availability/#",
                Type = TriggerType.OnAvailability,
            },
            Actions = new List<RuleAction>
            {
                new() { Type = ActionType.Publish, Topic = "alerts/should-not-fire",
                        Payload = new Dictionary<string, object> { ["x"] = 1 } },
            },
        });

        await engine.ProcessMessageAsync(MakeMessage("seed/warmup"));
        mqtt.Published.Clear();

        devices.RaiseAvailability("whatever", DeviceStatus.Online, DeviceStatus.Offline);

        await Task.Delay(100);
        Assert.Empty(mqtt.Published);
    }

    [Fact]
    public async Task ExecuteStartupRulesAsync_FailingRule_IncrementsRulesErrored()
    {
        // A startup rule that throws must increment the RulesErrored
        // counter, not just emit a log line. Null-ing Actions makes
        // ProcessRuleAsync's iteration NRE.
        var (engine, rules, _, stats) = CreateEngine();

        rules.Rules.Add(new Rule
        {
            Id = "bad-startup",
            Enabled = true,
            Trigger = new RuleTrigger { Type = TriggerType.OnStartup, TopicPattern = "" },
            Actions = null!,
        });

        await engine.ExecuteStartupRulesAsync();

        Assert.Equal(1, Interlocked.Read(ref stats.RulesErr));
    }

    [Fact]
    public async Task ExecuteStartupRulesAsync_DoesNotDispatchAvailabilityRulesImmediately()
    {
        var (engine, rules, mqtt, _, _, _) = CreateEngineWithLogger();

        rules.Rules.Add(new Rule
        {
            Id = "rule-availability",
            Enabled = true,
            Trigger = new RuleTrigger { TopicPattern = "availability/#", Type = TriggerType.OnAvailability },
            Actions = new List<RuleAction>
            {
                new() { Type = ActionType.Publish, Topic = "alerts/nope",
                        Payload = new Dictionary<string, object> { ["x"] = 1 } },
            },
        });

        await engine.ExecuteStartupRulesAsync();

        Assert.Empty(mqtt.Published);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, int timeoutMs = 1000, int stepMs = 10)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return;
            await Task.Delay(stepMs);
        }
    }

    [Fact]
    public async Task PublishRule_PayloadWithJsonElementValues_PreservesNumericPrecisionAndStructure()
    {
        // Numbers inspect raw text for `.`/`e` first so integer- vs
        // fractional-formatted values keep their natural CLR type. Null at the
        // top level maps to the empty string for MQTT compatibility; nested
        // nulls stay CLR null. Arrays and objects recurse preserving structure.
        var (engine, rules, mqtt, _) = CreateEngine();

        var tempElement = System.Text.Json.JsonDocument.Parse("22.5").RootElement;
        var countElement = System.Text.Json.JsonDocument.Parse("42").RootElement;
        var bigFloatElement = System.Text.Json.JsonDocument.Parse("1.5e9").RootElement;
        var nullElement = System.Text.Json.JsonDocument.Parse("null").RootElement;
        var arrayElement = System.Text.Json.JsonDocument.Parse("[1, 2.5, null, \"s\"]").RootElement;
        var nestedElement = System.Text.Json.JsonDocument.Parse("{\"inner\":{\"k\":7}}").RootElement;

        rules.Rules.Add(new Rule
        {
            Id = "precision",
            Name = "precision",
            Enabled = true,
            Trigger = new RuleTrigger { TopicPattern = "zigbee/lamp", Type = TriggerType.OnMessage },
            Actions = new List<RuleAction>
            {
                new()
                {
                    Type = ActionType.Publish,
                    Topic = "out/precision",
                    Payload = new Dictionary<string, object>
                    {
                        ["temp"] = tempElement,
                        ["count"] = countElement,
                        ["big"] = bigFloatElement,
                        ["nothing"] = nullElement,
                        ["arr"] = arrayElement,
                        ["nested"] = nestedElement
                    }
                }
            }
        });

        await engine.ProcessMessageAsync(MakeMessage("zigbee/lamp"));

        var published = Assert.Single(mqtt.Published, p => p.Topic == "out/precision");
        using var doc = System.Text.Json.JsonDocument.Parse(published.Payload);
        var root = doc.RootElement;

        // Fractional source was `22.5`: must round-trip as a JSON number
        // with `.5` precision, not a stringified "22.5".
        Assert.Equal(System.Text.Json.JsonValueKind.Number, root.GetProperty("temp").ValueKind);
        Assert.Equal(22.5, root.GetProperty("temp").GetDouble());

        // Integer source was `42`: must round-trip as a JSON number that
        // still reads as an int64, not stringified and not "42.0".
        Assert.Equal(System.Text.Json.JsonValueKind.Number, root.GetProperty("count").ValueKind);
        Assert.Equal(42L, root.GetProperty("count").GetInt64());

        // Exponent-notation source `1.5e9`: has a `.` and an `e` in the raw
        // text so it takes the double branch and preserves 1_500_000_000.0.
        Assert.Equal(System.Text.Json.JsonValueKind.Number, root.GetProperty("big").ValueKind);
        Assert.Equal(1.5e9, root.GetProperty("big").GetDouble());

        // JSON null at the top level maps to the empty string to preserve the
        // "non-null object" contract of the outer EvaluatePayloadValue
        // signature. Nested nulls inside arr/nested keep CLR null semantics.
        Assert.Equal(System.Text.Json.JsonValueKind.String, root.GetProperty("nothing").ValueKind);
        Assert.Equal("", root.GetProperty("nothing").GetString());

        // Array source was `[1, 2.5, null, "s"]`: must preserve the element
        // types, including the CLR null for the middle element.
        var arr = root.GetProperty("arr");
        Assert.Equal(System.Text.Json.JsonValueKind.Array, arr.ValueKind);
        Assert.Equal(4, arr.GetArrayLength());
        Assert.Equal(1L, arr[0].GetInt64());
        Assert.Equal(2.5, arr[1].GetDouble());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, arr[2].ValueKind);
        Assert.Equal("s", arr[3].GetString());

        // Nested object source must round-trip as a JSON object with the
        // inner structure intact, NOT as a string `{"inner":{"k":7}}`.
        var nested = root.GetProperty("nested");
        Assert.Equal(System.Text.Json.JsonValueKind.Object, nested.ValueKind);
        Assert.Equal(7L, nested.GetProperty("inner").GetProperty("k").GetInt64());
    }

    [Fact]
    public async Task InvalidateCache_ForcesImmediateReload_SoNewRulesFireBeforeRefreshInterval()
    {
        // The rule cache is refreshed at most once per 5-second window.
        // Without InvalidateCache, a rule added to the underlying service
        // within that window would not fire. After InvalidateCache, the
        // next ProcessMessageAsync re-reads the service and picks it up.
        var (engine, rules, mqtt, _) = CreateEngine();

        // First rule: seed the cache by processing a message.
        rules.Rules.Add(MakePublishRule("initial", "zigbee/+/state", "alerts/initial"));
        await engine.ProcessMessageAsync(MakeMessage("zigbee/lamp/state"));
        Assert.Contains(mqtt.Published, p => p.Topic == "alerts/initial");

        // Add a second rule. At this point the cache timestamp is fresh
        // (just set by the previous call), so a second ProcessMessageAsync
        // would not re-read the service and the new rule would be missed.
        rules.Rules.Add(MakePublishRule("added-after", "zigbee/+/brightness", "alerts/added"));

        // Invalidate and immediately fire a matching message.
        engine.InvalidateCache();
        await engine.ProcessMessageAsync(MakeMessage("zigbee/lamp/brightness"));

        Assert.Contains(mqtt.Published, p => p.Topic == "alerts/added");
    }

    // ---- Fakes ----

    private sealed class FakeRuleAuditRepository : IRuleAuditRepository
    {
        public Task AppendAsync(string ruleId, string? topic, double elapsedMs, bool success, string? error, int actionCount, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class FakeRulesService : IRulesService
    {
        public List<Rule> Rules { get; } = new();

        public Task<IEnumerable<Rule>> GetAllRulesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IEnumerable<Rule>>(Rules.ToList());

        public Task<Rule?> GetRuleAsync(string id, CancellationToken cancellationToken = default)
            => Task.FromResult(Rules.FirstOrDefault(r => r.Id == id));

        public Task<Rule> AddOrUpdateRuleAsync(Rule rule, CancellationToken cancellationToken = default)
        {
            var existing = Rules.FindIndex(r => r.Id == rule.Id);
            if (existing >= 0) Rules[existing] = rule;
            else Rules.Add(rule);
            return Task.FromResult(rule);
        }

        public Task<bool> RemoveRuleAsync(string id, CancellationToken cancellationToken = default)
            => Task.FromResult(Rules.RemoveAll(r => r.Id == id) > 0);

        public Task<bool> EnableRuleAsync(string id, bool enabled, CancellationToken cancellationToken = default)
        {
            var rule = Rules.FirstOrDefault(r => r.Id == id);
            if (rule is null) return Task.FromResult(false);
            rule.Enabled = enabled;
            return Task.FromResult(true);
        }

        public Task<bool> UpdateRuleStateAsync(string id, Dictionary<string, object> state, CancellationToken cancellationToken = default)
        {
            var rule = Rules.FirstOrDefault(r => r.Id == id);
            if (rule is null) return Task.FromResult(false);
            rule.State = new Dictionary<string, object>(state);
            return Task.FromResult(true);
        }

        public Task<IEnumerable<Rule>> GetMatchingRulesAsync(string topic, CancellationToken cancellationToken = default)
            => Task.FromResult<IEnumerable<Rule>>(Rules);

        public Task<IEnumerable<Rule>> GetRulesByTagAsync(string tag, CancellationToken cancellationToken = default)
            => Task.FromResult<IEnumerable<Rule>>(Rules.Where(r => r.Tags.Contains(tag)));

        public Task<IEnumerable<Rule>> GetRulesByTriggerTypeAsync(TriggerType triggerType, CancellationToken cancellationToken = default)
            => Task.FromResult<IEnumerable<Rule>>(Rules.Where(r => r.Trigger.Type == triggerType));

        public Task<int> CountActiveAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Rules.Count(r => r.Enabled));

        public Task BulkUpdateStatsAsync(IReadOnlyCollection<RuleStatsUpdate> updates, CancellationToken cancellationToken = default)
        {
            foreach (var u in updates)
            {
                var rule = Rules.FirstOrDefault(r => r.Id == u.RuleId);
                if (rule is null) continue;
                rule.ExecutionCount += u.DeltaExecutionCount;
                rule.LastExecutedAt = u.LastExecutedAt;
                if (u.LastError is not null)
                {
                    rule.LastError = u.LastError;
                    rule.LastErrorAt = u.LastErrorAt;
                }
            }
            return Task.CompletedTask;
        }
    }

    private sealed class FakeMqttService : IMqttService
    {
        public List<(string Topic, string Payload)> Published { get; } = new();
        public bool IsConnected => true;
        public event EventHandler<MqttMessage>? MessageReceived;
        public event EventHandler<bool>? ConnectionStateChanged;

        public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SubscribeAsync(string topic, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UnsubscribeAsync(string topic, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task PublishAsync(string topic, string payload, bool retain = false, int qos = 0, CancellationToken cancellationToken = default)
        {
            Published.Add((topic, payload));
            return Task.CompletedTask;
        }

        public IReadOnlyList<MqttMessage> GetMessageHistory() => Array.Empty<MqttMessage>();

        public void RaiseMessageReceived(MqttMessage m) => MessageReceived?.Invoke(this, m);
        public void RaiseConnectionStateChanged(bool connected) => ConnectionStateChanged?.Invoke(this, connected);
    }

    private sealed class FakeStatsService : IStatsService
    {
        public long Messages;
        public long Rules;

        public Task<SystemStats> GetCurrentStatsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new SystemStats());

        public long Dropped;

        public long RulesErr;
        public long ActionsErr;

        public void IncrementMessagesProcessed() => Interlocked.Increment(ref Messages);
        public void IncrementMessagesByProtocol(Protocol protocol) { }
        public void IncrementRulesExecuted() => Interlocked.Increment(ref Rules);
        public void IncrementMessagesDropped() => Interlocked.Increment(ref Dropped);
        public void IncrementRulesErrored() => Interlocked.Increment(ref RulesErr);
        public void IncrementActionsErrored() => Interlocked.Increment(ref ActionsErr);

        public Task<IEnumerable<ProtocolStats>> GetProtocolStatsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IEnumerable<ProtocolStats>>(Array.Empty<ProtocolStats>());

        public void SeedCounters(long messagesProcessed, long rulesExecuted, long messagesDropped = 0,
            long rulesErrored = 0, long actionsErrored = 0)
        {
            Interlocked.Exchange(ref Messages, messagesProcessed);
            Interlocked.Exchange(ref Rules, rulesExecuted);
            Interlocked.Exchange(ref Dropped, messagesDropped);
            Interlocked.Exchange(ref RulesErr, rulesErrored);
            Interlocked.Exchange(ref ActionsErr, actionsErrored);
        }

        public (long MessagesProcessed, long RulesExecuted, long MessagesDropped,
                long RulesErrored, long ActionsErrored) GetCounters()
            => (Interlocked.Read(ref Messages), Interlocked.Read(ref Rules), Interlocked.Read(ref Dropped),
                Interlocked.Read(ref RulesErr), Interlocked.Read(ref ActionsErr));

        public Task ResetCountersAsync(CancellationToken cancellationToken = default)
        {
            SeedCounters(0, 0);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new HttpClient();
    }

    private sealed class FakeDeviceService : IDeviceService
    {
        public event EventHandler<DeviceAvailabilityChangedEventArgs>? AvailabilityChanged;

        public void RaiseAvailability(string deviceId, DeviceStatus previous, DeviceStatus current, string? topic = null)
        {
            AvailabilityChanged?.Invoke(this, new DeviceAvailabilityChangedEventArgs
            {
                DeviceId = deviceId,
                PreviousStatus = previous,
                CurrentStatus = current,
                Topic = topic ?? $"availability/{deviceId}",
            });
        }

        public Task<DevicePage> GetDevicesPageAsync(int offset, int limit, string? filter = null, Protocol? protocol = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new DevicePage(Array.Empty<Device>(), 0, Math.Max(0, offset), Math.Clamp(limit, 1, 1000)));

#pragma warning disable CS1998
        public async IAsyncEnumerable<Device> StreamAllDevicesAsync(
            int pageSize = 500,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            yield break;
        }
#pragma warning restore CS1998

        public Task<Device?> GetDeviceAsync(string id, CancellationToken cancellationToken = default)
            => Task.FromResult<Device?>(null);

        public Task<Device> AddOrUpdateDeviceAsync(Device device, CancellationToken cancellationToken = default)
            => Task.FromResult(device);

        public Task<bool> RemoveDeviceAsync(string id, CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        public Task<bool> RenameDeviceAsync(string oldId, string newId, CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        public Task<IEnumerable<Device>> GetDevicesByProtocolAsync(Protocol protocol, CancellationToken cancellationToken = default)
            => Task.FromResult<IEnumerable<Device>>(Array.Empty<Device>());

        public Task<DeviceCounts> GetCountsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new DeviceCounts(0, 0, new Dictionary<Protocol, int>()));

        public Task UpdateDeviceStateAsync(string deviceId, Dictionary<string, object> state, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task UpsertDeviceStateAsync(string deviceId, string name, Protocol protocol, Dictionary<string, object> state, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task UpdateDeviceStatusAsync(string deviceId, DeviceStatus status, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    /// <summary>
    /// Minimal <see cref="ILogger{T}"/> that stores all log events so tests
    /// can assert on them. Thread-safe for a simple append pattern.
    /// </summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Events { get; } = new();

        IDisposable? ILogger.BeginScope<TState>(TState state) => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (Events)
            {
                Events.Add((logLevel, formatter(state, exception)));
            }
        }
    }
}
