using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Hermod.Core;
using Hermod.Core.Interfaces;
using Hermod.Core.Models.Rules;
using Hermod.Core.Telemetry;
using Hermod.Rules.Payload;
using Hermod.Rules.Security;
using Microsoft.Extensions.Logging;

namespace Hermod.Rules;

public sealed partial class EnhancedRulesEngine
{
    // Wall-clock Unix ns; same clock as MessageProcessor's stamper.
    private static long UnixNanoseconds()
    {
        return (DateTime.UtcNow.Ticks - DateTime.UnixEpoch.Ticks) * 100L;
    }

    private async Task ProcessRuleAsync(
        Rule rule,
        ProcessedMessage? message,
        Dictionary<string, object>? chainData,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        if (!string.IsNullOrEmpty(rule.Trigger.Debounce) && !CheckDebounce(rule))
        {
            _logger.LogDebug("Rule {RuleId} debounced", rule.Id);
            return;
        }

        if (rule.Trigger.ActiveWindows?.Count > 0 && !IsInActiveWindow(rule.Trigger.ActiveWindows))
        {
            _logger.LogDebug("Rule {RuleId} outside active time window", rule.Id);
            return;
        }

        var context = BuildContext(rule, message, chainData);

        if (!_conditionEvaluator.Evaluate(rule.Conditions, context))
        {
            _logger.LogDebug("Rule {RuleId} conditions not met", rule.Id);
            return;
        }

        if (rule.Trigger.Type == TriggerType.OnChange && message?.DeviceName is not null)
        {
            var previous = _stateManager.GetPreviousDeviceState(message.DeviceName);
            if (previous is not null)
            {
                context = context with { Previous = previous };

                var hasChange = message.ParsedPayload?.Any(kvp =>
                    !previous.TryGetValue(kvp.Key, out var prevVal) ||
                    !Coercion.NumericCoercion.LooseEquals(kvp.Value, prevVal)) ?? false;

                if (!hasChange)
                {
                    _logger.LogDebug("Rule {RuleId} skipped: no changes detected", rule.Id);
                    return;
                }
            }
        }

        List<RuleActionResult> actionResults = [];

        foreach (var action in rule.Actions)
        {
            // Honour shutdown at the orchestrator level: without this, inner
            // actions see the cancelled token and return failure results, but
            // the loop keeps going and logs a suppressed-warning per action.
            if (cancellationToken.IsCancellationRequested) break;

            var result = await ExecuteActionAsync(action, rule, context, cancellationToken);
            actionResults.Add(result);

            if (action.Type == ActionType.SetState)
            {
                context = context with { State = _stateManager.GetRuleState(rule.Id) };
            }

            if (!result.Success && action.Type != ActionType.Log)
            {
                _statsService.IncrementActionsErrored();
                // Log-once per (rule, action, error) so a bad rule
                // doesn't flood the log; counter still ticks silently.
                var key = (rule.Id, action.Type, result.Error ?? "");
                if (_reportedActionFailures.TryAdd(key, 0))
                {
                    _logger.LogWarning(
                        "Action {ActionType} failed for rule {RuleId}: {Error} (further occurrences suppressed; see hermod_action_errors_total)",
                        action.Type, rule.Id, result.Error);
                }
            }

            if (action.Type == ActionType.Stop) break;
        }

        stopwatch.Stop();

        RecordRuleExecution(rule.Id);
        _statsService.IncrementRulesExecuted();
        _metrics.ObserveRuleEvalSeconds(stopwatch.Elapsed.TotalSeconds);

        if (_settings.Features.RuleAuditLog)
        {
            var firstFailure = actionResults.FirstOrDefault(r => !r.Success);
            await _ruleAudit.AppendAsync(
                rule.Id,
                message?.OriginalMessage.Topic,
                stopwatch.Elapsed.TotalMilliseconds,
                success: firstFailure is null,
                error: firstFailure?.Error,
                actionCount: actionResults.Count,
                cancellationToken: cancellationToken);
            // IncRuleAuditWrites moved into PostgresRuleAuditRepository.FlushAsync
            // so the counter only bumps when the row actually commits to PG —
            // enqueue-time counting lied during flush failures and DropOldest.
        }

        if (_settings.Engine.LogBatching)
        {
            AccumulateExecutionLog(rule.Id, stopwatch.Elapsed.TotalMilliseconds);
        }
        else if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("Rule {RuleId} executed in {ElapsedMs}ms", rule.Id, stopwatch.ElapsedMilliseconds);
        }

        RuleExecuted?.Invoke(this, new RuleExecutedEventArgs
        {
            Rule = rule,
            Message = message ?? EmptyMessage,
            ActionResults = actionResults,
            ExecutionTime = stopwatch.Elapsed,
        });
    }

    // Bounded recursion: Conditional/Parallel cycles cannot loop forever.
    private const int MaxActionDepth = 16;

    private async Task<RuleActionResult> ExecuteActionAsync(
        RuleAction action,
        Rule rule,
        ExpressionContext context,
        CancellationToken cancellationToken,
        int depth = 0)
    {
        if (depth > MaxActionDepth)
        {
            return new RuleActionResult
            {
                Action = action,
                Success = false,
                Error = $"Action depth {depth} exceeded MaxActionDepth ({MaxActionDepth})",
            };
        }

        var startTimestamp = Stopwatch.GetTimestamp();
        RuleActionResult result;

        try
        {
            result = action.Type switch
            {
                ActionType.Publish => await ExecutePublishAsync(action, context, cancellationToken),
                ActionType.Chain => await ExecuteChainAsync(action, context, cancellationToken),
                ActionType.SetState => ExecuteSetState(action, rule, context),
                ActionType.Delay => ExecuteDelay(action, rule, context),
                ActionType.CancelDelay => ExecuteCancelDelay(action, rule),
                ActionType.Conditional => await ExecuteConditionalAsync(action, rule, context, depth, cancellationToken),
                ActionType.Parallel => await ExecuteParallelAsync(action, rule, context, depth, cancellationToken),
                ActionType.Log => ExecuteLog(action, context),
                ActionType.Webhook => await ExecuteWebhookAsync(action, context, cancellationToken),
                ActionType.Stop => new RuleActionResult { Action = action, Success = true },
                _ => new RuleActionResult
                {
                    Action = action,
                    Success = false,
                    Error = $"Unknown action type: {action.Type}",
                },
            };
        }
#pragma warning disable CA1031 // action-level failures are captured into RuleActionResult so rule dispatch can continue with subsequent actions
        catch (Exception ex)
#pragma warning restore CA1031
        {
            result = new RuleActionResult
            {
                Action = action,
                Success = false,
                Error = ex.Message,
            };
        }

        return result.WithExecutionTime(Stopwatch.GetElapsedTime(startTimestamp));
    }

    private async Task<RuleActionResult> ExecutePublishAsync(
        RuleAction action,
        ExpressionContext context,
        CancellationToken cancellationToken)
    {
        var topic = _expressionEvaluator.Evaluate<string>(action.Topic ?? "", context) ?? "";

        var payload = action.PassthroughPayload
            ? JsonPayloadConverter.DeepCloneDictionary(context.Source)
            : new Dictionary<string, object>();

        if (action.Payload is not null)
        {
            foreach (var kvp in action.Payload)
            {
                payload[kvp.Key] = _payloadConverter.EvaluatePayloadValue(kvp.Value, context);
            }
        }

        if (action.Transforms is not null)
        {
            foreach (var transform in action.Transforms)
            {
                ApplyTransform(transform, context, payload);
            }
        }

        var json = JsonSerializer.Serialize(payload);
        var qos = Math.Clamp(action.QoS, 0, 2);
        _metrics.IncRulePublishAttempted();
        try
        {
            await _mqttService.PublishAsync(topic, json, retain: action.Retain, qos: qos, cancellationToken);
        }
        catch (Exception ex)
        {
            _metrics.IncRulePublishFailed();
            _logger.LogWarning(ex, "Rule publish failed for topic {Topic}", topic);
            throw;
        }

        if (context.TraceUuid is { } trace)
        {
            _timestampRecorder.Record(trace, TimestampStages.ActionPublish, UnixNanoseconds());
        }

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("Published to {Topic}: {Payload}", topic, json);
        }

        return new RuleActionResult
        {
            Action = action,
            Success = true,
            Result = new { Topic = topic, Payload = payload, QoS = qos, action.Retain },
        };
    }

    private async Task<RuleActionResult> ExecuteChainAsync(
        RuleAction action,
        ExpressionContext context,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(action.ChainToRule))
        {
            return new RuleActionResult { Action = action, Success = false, Error = "ChainToRule not specified" };
        }

        var chainData = BuildChainData(action.ChainData, context);

        if (!string.IsNullOrEmpty(action.Delay))
        {
            var delay = DelayParser.Parse(action.Delay);
            var scheduleId = _scheduler.ScheduleDelay(action.ChainToRule, delay, chainData);
            return new RuleActionResult
            {
                Action = action,
                Success = true,
                Result = new { ScheduleId = scheduleId, ChainedRule = action.ChainToRule },
            };
        }

        await TriggerRuleAsync(action.ChainToRule, chainData, null, cancellationToken);

        return new RuleActionResult
        {
            Action = action,
            Success = true,
            Result = new { ChainedRule = action.ChainToRule },
        };
    }

    private RuleActionResult ExecuteSetState(RuleAction action, Rule rule, ExpressionContext context)
    {
        if (action.SetState is not null)
        {
            foreach (var kvp in action.SetState)
            {
                _stateManager.SetRuleState(rule.Id, kvp.Key, _payloadConverter.EvaluatePayloadValue(kvp.Value, context));
            }
        }

        if (action.GlobalState is not null)
        {
            foreach (var kvp in action.GlobalState)
            {
                _stateManager.SetGlobal(kvp.Key, _payloadConverter.EvaluatePayloadValue(kvp.Value, context));
            }
        }

        return new RuleActionResult { Action = action, Success = true };
    }

    private RuleActionResult ExecuteDelay(RuleAction action, Rule rule, ExpressionContext context)
    {
        if (string.IsNullOrEmpty(action.Delay) || string.IsNullOrEmpty(action.ChainToRule))
        {
            return new RuleActionResult
            {
                Action = action,
                Success = false,
                Error = "Delay and ChainToRule required for Delay action",
            };
        }

        var delay = DelayParser.Parse(action.Delay);
        var chainData = BuildChainData(action.ChainData, context);
        var scheduleId = _scheduler.ScheduleDelay(action.ChainToRule, delay, chainData);

        _stateManager.SetRuleState(rule.Id, $"schedule_{action.ChainToRule}", scheduleId);

        return new RuleActionResult
        {
            Action = action,
            Success = true,
            Result = new { ScheduleId = scheduleId },
        };
    }

    private RuleActionResult ExecuteCancelDelay(RuleAction action, Rule rule)
    {
        if (string.IsNullOrEmpty(action.ChainToRule))
        {
            return new RuleActionResult
            {
                Action = action,
                Success = false,
                Error = "ChainToRule required to identify delay to cancel",
            };
        }

        var key = $"schedule_{action.ChainToRule}";
        if (_stateManager.GetRuleState(rule.Id).TryGetValue(key, out var scheduleIdObj) &&
            scheduleIdObj is string scheduleId && !string.IsNullOrEmpty(scheduleId))
        {
            _scheduler.Cancel(scheduleId);
        }

        _scheduler.CancelForRule(action.ChainToRule);

        return new RuleActionResult { Action = action, Success = true };
    }

    private async Task<RuleActionResult> ExecuteConditionalAsync(
        RuleAction action,
        Rule rule,
        ExpressionContext context,
        int depth,
        CancellationToken cancellationToken)
    {
        var conditionMet = _conditionEvaluator.Evaluate(action.If, context);
        var actionsToExecute = conditionMet ? action.Then : action.Else;

        if (actionsToExecute is null || actionsToExecute.Count == 0)
        {
            return new RuleActionResult { Action = action, Success = true, Result = new { ConditionMet = conditionMet } };
        }

        foreach (var subAction in actionsToExecute)
        {
            var result = await ExecuteActionAsync(subAction, rule, context, cancellationToken, depth + 1);
            if (!result.Success)
            {
                return result;
            }
        }

        return new RuleActionResult
        {
            Action = action,
            Success = true,
            Result = new { ConditionMet = conditionMet },
        };
    }

    private async Task<RuleActionResult> ExecuteParallelAsync(
        RuleAction action,
        Rule rule,
        ExpressionContext context,
        int depth,
        CancellationToken cancellationToken)
    {
        if (action.ParallelActions is null || action.ParallelActions.Count == 0)
        {
            return new RuleActionResult { Action = action, Success = true };
        }

        var parallelism = _settings.Engine.Parallelism;
        var results = new RuleActionResult[action.ParallelActions.Count];

        if (parallelism <= 0 || parallelism >= action.ParallelActions.Count)
        {
            // Unbounded fan-out (default when Engine:Parallelism is 0).
            var tasks = new List<Task<RuleActionResult>>(action.ParallelActions.Count);
            foreach (var parallelAction in action.ParallelActions)
            {
                tasks.Add(ExecuteActionAsync(parallelAction, rule, context, cancellationToken, depth + 1));
            }
            var awaited = await Task.WhenAll(tasks);
            for (var i = 0; i < awaited.Length; i++) results[i] = awaited[i];
        }
        else
        {
            // Bounded fan-out per Engine:Parallelism. Semaphore=1 is the
            // sequential measurement profile.
            using var limiter = new SemaphoreSlim(parallelism, parallelism);
            var tasks = new List<Task>(action.ParallelActions.Count);
            // 60s ceiling prevents a runaway action from wedging the engine.
            var slotTimeout = TimeSpan.FromSeconds(60);
            try
            {
                for (var i = 0; i < action.ParallelActions.Count; i++)
                {
                    var idx = i;
                    var parallelAction = action.ParallelActions[idx];
                    var acquired = await limiter.WaitAsync(slotTimeout, cancellationToken);
                    if (!acquired)
                    {
                        results[idx] = new RuleActionResult
                        {
                            Action = parallelAction,
                            Success = false,
                            Error = $"Parallel slot acquire timed out after {slotTimeout.TotalSeconds:F0}s",
                        };
                        continue;
                    }
                    tasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            results[idx] = await ExecuteActionAsync(
                                parallelAction, rule, context, cancellationToken, depth + 1);
                        }
                        finally
                        {
                            limiter.Release();
                        }
                    }, cancellationToken));
                }
            }
            finally
            {
                // Always drain launched tasks before unwinding. If the outer
                // token cancels mid-loop, WaitAsync throws OCE — without this,
                // already-launched Task.Run tasks would run unobserved against
                // a disposed semaphore and faultedly release onto ODE.
                if (tasks.Count > 0)
                {
#pragma warning disable CA1031 // per-task failures are captured into the results array; drain swallows to guarantee no orphan continues post-exit
                    try { await Task.WhenAll(tasks); }
                    catch { /* per-task failures are captured into the results array */ }
#pragma warning restore CA1031
                }
            }
        }

        return new RuleActionResult
        {
            Action = action,
            Success = results.All(r => r.Success),
            Result = results,
        };
    }

    private RuleActionResult ExecuteLog(RuleAction action, ExpressionContext context)
    {
        var message = _expressionEvaluator.Evaluate<string>(action.LogMessage ?? "", context) ?? "";
        var level = ParseLogLevel(action.LogLevel);
        _logger.Log(level, "[Rule Log] {Message}", message);

        return new RuleActionResult
        {
            Action = action,
            Success = true,
            Result = message,
        };
    }

    private async Task<RuleActionResult> ExecuteWebhookAsync(
        RuleAction action,
        ExpressionContext context,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(action.WebhookUrl))
        {
            return new RuleActionResult { Action = action, Success = false, Error = "WebhookUrl not specified" };
        }

        var url = _expressionEvaluator.Evaluate<string>(action.WebhookUrl, context) ?? "";

        var (ok, rejection) = await WebhookHostGuard.TryValidateAsync(url, cancellationToken);
        if (!ok)
        {
            return new RuleActionResult
            {
                Action = action,
                Success = false,
                Error = $"Webhook URL rejected by SSRF guard: {url} ({rejection})",
            };
        }

        var payload = BuildWebhookPayload(action.Payload, context);
        var json = JsonSerializer.Serialize(payload);

        using var request = new HttpRequestMessage(ParseHttpMethod(action.WebhookMethod), url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

        var client = _httpClientFactory.CreateClient(WebhookHttpClientName);
        using var response = await client.SendAsync(request, cancellationToken);

        return new RuleActionResult
        {
            Action = action,
            Success = response.IsSuccessStatusCode,
            Result = new { StatusCode = (int)response.StatusCode, Url = url },
        };
    }

    private void ApplyTransform(PayloadTransform transform, ExpressionContext context, Dictionary<string, object> payload)
    {
        if (!string.IsNullOrEmpty(transform.SourceProperty) && !string.IsNullOrEmpty(transform.TargetProperty))
        {
            if (context.Source.TryGetValue(transform.SourceProperty, out var sourceValue))
            {
                payload[transform.TargetProperty] = _payloadConverter.EvaluatePayloadValue(sourceValue, context);
            }
            else if (transform.DefaultValue is not null)
            {
                payload[transform.TargetProperty] = transform.DefaultValue;
            }
        }

        if (!string.IsNullOrEmpty(transform.Expression) && !string.IsNullOrEmpty(transform.TargetProperty))
        {
            var result = _expressionEvaluator.Evaluate(transform.Expression, context);
            if (result is not null)
            {
                payload[transform.TargetProperty] = result;
            }
        }
    }

    private Dictionary<string, object> BuildChainData(
        Dictionary<string, object>? source,
        ExpressionContext context)
    {
        if (source is null) return [];

        var data = new Dictionary<string, object>(source.Count);
        foreach (var kvp in source)
        {
            data[kvp.Key] = _payloadConverter.EvaluatePayloadValue(kvp.Value, context);
        }
        return data;
    }

    private Dictionary<string, object> BuildWebhookPayload(
        Dictionary<string, object>? source,
        ExpressionContext context)
    {
        if (source is null) return [];

        var data = new Dictionary<string, object>(source.Count);
        foreach (var kvp in source)
        {
            data[kvp.Key] = _payloadConverter.EvaluatePayloadValue(kvp.Value, context);
        }
        return data;
    }

    private static HttpMethod ParseHttpMethod(string? method) =>
        string.IsNullOrEmpty(method) ? HttpMethod.Post : method.ToUpperInvariant() switch
        {
            "GET" => HttpMethod.Get,
            "POST" => HttpMethod.Post,
            "PUT" => HttpMethod.Put,
            "PATCH" => HttpMethod.Patch,
            "DELETE" => HttpMethod.Delete,
            _ => HttpMethod.Post,
        };

    private static LogLevel ParseLogLevel(string? level) =>
        // CA1308 prefers ToUpperInvariant for culture-insensitive string dispatch.
        string.IsNullOrEmpty(level) ? LogLevel.Information : level.ToUpperInvariant() switch
        {
            "TRACE" => LogLevel.Trace,
            "DEBUG" => LogLevel.Debug,
            "INFO" or "INFORMATION" => LogLevel.Information,
            "WARN" or "WARNING" => LogLevel.Warning,
            "ERROR" => LogLevel.Error,
            "CRITICAL" or "CRIT" => LogLevel.Critical,
            _ => LogLevel.Information,
        };

    private static readonly ProcessedMessage EmptyMessage = new()
    {
        OriginalMessage = new() { Topic = "", Payload = "" },
        ShouldTriggerRules = false,
    };
}
