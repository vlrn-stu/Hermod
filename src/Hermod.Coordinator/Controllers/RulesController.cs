using Hermod.Core.Interfaces;
using Hermod.Core.Models.Rules;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Hermod.Coordinator.Controllers;

/// <summary>REST surface over the rule engine: CRUD, enable toggling and cache invalidation.</summary>
[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
[Authorize]
public sealed class RulesController : ControllerBase
{
    private readonly IRulesService _rulesService;
    private readonly IRulesEngine _rulesEngine;
    private readonly ILogger<RulesController> _logger;

    /// <summary>Creates the controller with its rules service, evaluation engine and logger.</summary>
    /// <param name="rulesService">Rule store.</param>
    /// <param name="rulesEngine">Rule evaluation engine whose cache is invalidated on writes.</param>
    /// <param name="logger">Logger for rule lifecycle events.</param>
    public RulesController(IRulesService rulesService, IRulesEngine rulesEngine, ILogger<RulesController> logger)
    {
        ArgumentNullException.ThrowIfNull(rulesService);
        ArgumentNullException.ThrowIfNull(rulesEngine);
        ArgumentNullException.ThrowIfNull(logger);
        _rulesService = rulesService;
        _rulesEngine = rulesEngine;
        _logger = logger;
    }

    private string ActorName => User?.Identity?.Name ?? "unknown";

    /// <summary>Get all rules, optionally filtered by enabled state.</summary>
    /// <param name="enabled">Optional enabled-state filter.</param>
    /// <param name="cancellationToken">Token to abort the query.</param>
    /// <returns>200 with the matching rules.</returns>
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<Rule>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<Rule>>> GetAll(
        [FromQuery] bool? enabled,
        CancellationToken cancellationToken = default)
    {
        var rules = await _rulesService.GetAllRulesAsync(cancellationToken);
        if (enabled.HasValue)
        {
            rules = rules.Where(r => r.Enabled == enabled.Value);
        }
        return Ok(rules);
    }

    /// <summary>Get rule by ID.</summary>
    /// <param name="id">Rule identifier.</param>
    /// <param name="cancellationToken">Token to abort the query.</param>
    /// <returns>200 with the rule, 404 if missing.</returns>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(Rule), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<Rule>> GetById(string id, CancellationToken cancellationToken = default)
    {
        var rule = await _rulesService.GetRuleAsync(id, cancellationToken);
        return rule is null ? NotFoundForRule(id) : Ok(rule);
    }

    /// <summary>Create a new rule.</summary>
    /// <param name="request">Rule definition. Name is required; other fields fall back to safe defaults.</param>
    /// <param name="cancellationToken">Token to abort the create.</param>
    /// <returns>201 at <c>GetById</c> on success, 400 when <c>Name</c> is empty.</returns>
    [HttpPost]
    [Authorize(Policy = Hermod.Coordinator.Authorization.Policies.Operator)]
    [ProducesResponseType(typeof(Rule), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<Rule>> Create(
        [FromBody] CreateRuleRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrEmpty(request.Name))
        {
            return BadRequest(new { message = "Rule name is required" });
        }

        // Architectural ceiling — see HermodLimits.MaxRules. Reads from the
        // engine's cached count (no DB hit per POST). Slightly stale on
        // bursts, so a tight POST loop can over-shoot by a small amount
        // before the next cache rebuild closes the gate.
        if (_rulesEngine.TotalRuleCount >= Hermod.Core.Configuration.HermodLimits.MaxRules)
        {
            return Conflict(new
            {
                message = $"Rule limit reached ({Hermod.Core.Configuration.HermodLimits.MaxRules}); delete unused rules before creating new ones",
            });
        }

        var ruleId = string.IsNullOrWhiteSpace(request.Id)
            ? Guid.NewGuid().ToString()
            : request.Id.Trim();

        // Idempotency: a client-supplied id that already exists is a duplicate
        // POST; return 409 so the caller can GET/PUT instead. Without this
        // check a retried POST silently upserts and grows duplicate rules.
        if (!string.IsNullOrWhiteSpace(request.Id))
        {
            var existing = await _rulesService.GetRuleAsync(ruleId, cancellationToken);
            if (existing is not null)
            {
                return Conflict(new { message = $"Rule '{ruleId}' already exists; use PUT to update" });
            }
        }

        var rule = new Rule
        {
            Id = ruleId,
            Name = request.Name,
            Description = request.Description ?? string.Empty,
            Enabled = request.Enabled,
            Trigger = request.Trigger ?? new RuleTrigger { TopicPattern = "#", Type = TriggerType.OnMessage },
            Conditions = request.Conditions,
            Actions = request.Actions ?? []
        };

        var result = await _rulesService.AddOrUpdateRuleAsync(rule, cancellationToken);
        _rulesEngine.InvalidateCache();
        _logger.LogInformation("Rule {RuleId} created via API by {Actor}", result.Id, ActorName);

        return CreatedAtAction(nameof(GetById), new { id = result.Id }, result);
    }

    /// <summary>Update an existing rule; null fields on the request preserve the current value.</summary>
    /// <param name="id">Rule identifier.</param>
    /// <param name="request">Fields to patch; null values leave the existing value untouched.</param>
    /// <param name="cancellationToken">Token to abort the update.</param>
    /// <returns>200 with the updated rule, 404 if missing.</returns>
    [HttpPut("{id}")]
    [Authorize(Policy = Hermod.Coordinator.Authorization.Policies.Operator)]
    [ProducesResponseType(typeof(Rule), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<Rule>> Update(
        string id,
        [FromBody] UpdateRuleRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var rule = await _rulesService.GetRuleAsync(id, cancellationToken);
        if (rule is null) return NotFoundForRule(id);

        rule.Name = request.Name ?? rule.Name;
        rule.Description = request.Description ?? rule.Description;
        rule.Enabled = request.Enabled ?? rule.Enabled;
        if (request.Trigger is not null) rule.Trigger = request.Trigger;
        if (request.Conditions is not null) rule.Conditions = request.Conditions;
        if (request.Actions is not null) rule.Actions = request.Actions;

        var result = await _rulesService.AddOrUpdateRuleAsync(rule, cancellationToken);
        _rulesEngine.InvalidateCache();
        _logger.LogInformation("Rule {RuleId} updated via API by {Actor}", id, ActorName);

        return Ok(result);
    }

    /// <summary>Enable or disable a rule.</summary>
    /// <param name="id">Rule identifier.</param>
    /// <param name="request">New enabled state.</param>
    /// <param name="cancellationToken">Token to abort the update.</param>
    /// <returns>200 on success, 404 if the rule is missing.</returns>
    [HttpPatch("{id}/enabled")]
    [Authorize(Policy = Hermod.Coordinator.Authorization.Policies.Operator)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> SetEnabled(
        string id,
        [FromBody] EnableRuleRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var success = await _rulesService.EnableRuleAsync(id, request.Enabled, cancellationToken);
        if (!success) return NotFoundForRule(id);

        _rulesEngine.InvalidateCache();
        _logger.LogInformation(
            "Rule {RuleId} {State} via API by {Actor}",
            id, request.Enabled ? "enabled" : "disabled", ActorName);
        return Ok(new { message = $"Rule {(request.Enabled ? "enabled" : "disabled")}" });
    }

    /// <summary>Delete a rule.</summary>
    /// <param name="id">Rule identifier.</param>
    /// <param name="cancellationToken">Token to abort the deletion.</param>
    /// <returns>200 on success, 404 if the rule is missing.</returns>
    [HttpDelete("{id}")]
    [Authorize(Policy = Hermod.Coordinator.Authorization.Policies.Operator)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> Delete(string id, CancellationToken cancellationToken = default)
    {
        var removed = await _rulesService.RemoveRuleAsync(id, cancellationToken);
        if (!removed) return NotFoundForRule(id);

        _rulesEngine.InvalidateCache();
        _logger.LogInformation("Rule {RuleId} deleted via API by {Actor}", id, ActorName);
        return Ok(new { message = "Rule deleted" });
    }

    private NotFoundObjectResult NotFoundForRule(string id) =>
        NotFound(new { message = $"Rule '{id}' not found" });
}

/// <summary>Body for <see cref="RulesController.Create"/>.</summary>
public sealed class CreateRuleRequest
{
    /// <summary>Optional client-supplied id for idempotent creation. When null/empty
    /// the server generates a GUID. Honored so test harnesses and seeding flows
    /// can produce deterministic ids — before this was added the server silently
    /// overwrote any supplied id with a fresh GUID, which meant every "upsert"
    /// POST was actually an insert and built up duplicate rules.</summary>
    public string? Id { get; set; }

    /// <summary>Human-readable rule name (required).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Optional longer description shown in the UI.</summary>
    public string? Description { get; set; }

    /// <summary>Whether the rule starts enabled (default: true).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Trigger definition; defaults to <c>OnMessage</c> on <c>#</c>.</summary>
    public RuleTrigger? Trigger { get; set; }

    /// <summary>Optional root condition group.</summary>
    public RuleConditionGroup? Conditions { get; set; }

    /// <summary>Actions to execute when the rule matches.</summary>
    public List<RuleAction>? Actions { get; set; }
}

/// <summary>Body for <see cref="RulesController.Update"/>; null fields leave the existing value unchanged.</summary>
public sealed class UpdateRuleRequest
{
    /// <summary>New name, or null to keep the existing one.</summary>
    public string? Name { get; set; }

    /// <summary>New description, or null to keep the existing one.</summary>
    public string? Description { get; set; }

    /// <summary>New enabled state, or null to keep the existing one.</summary>
    public bool? Enabled { get; set; }

    /// <summary>New trigger, or null to keep the existing one.</summary>
    public RuleTrigger? Trigger { get; set; }

    /// <summary>New condition group, or null to keep the existing one.</summary>
    public RuleConditionGroup? Conditions { get; set; }

    /// <summary>New action list, or null to keep the existing one.</summary>
    public List<RuleAction>? Actions { get; set; }
}

/// <summary>Body for <see cref="RulesController.SetEnabled"/>.</summary>
public sealed class EnableRuleRequest
{
    /// <summary>Target enabled state.</summary>
    public bool Enabled { get; set; }
}
