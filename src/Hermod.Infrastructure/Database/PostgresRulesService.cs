using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using Hermod.Core.Interfaces;
using Hermod.Core.Models.Rules;
using Hermod.Core.Mqtt;
using Microsoft.Extensions.Logging;

namespace Hermod.Infrastructure.Database;

/// <summary>
/// Persists rules in PostgreSQL via Dapper. JSONB columns hold nested rule
/// DTOs so the dashboard round-trips without a parallel relational model.
/// </summary>
public sealed class PostgresRulesService : IRulesService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private const string UpsertRuleSql = """
        INSERT INTO rules (id, name, description, enabled, priority, trigger, conditions, actions,
                           state, tags, execution_count, last_executed_at, last_error_at, last_error,
                           created_at, updated_at)
        VALUES (@Id, @Name, @Description, @Enabled, @Priority, @Trigger::jsonb, @Conditions::jsonb,
                @Actions::jsonb, @State::jsonb, @Tags::jsonb, @ExecutionCount, @LastExecutedAt,
                @LastErrorAt, @LastError, @CreatedAt, @UpdatedAt)
        ON CONFLICT(id) DO UPDATE SET
            name = EXCLUDED.name,
            description = EXCLUDED.description,
            enabled = EXCLUDED.enabled,
            priority = EXCLUDED.priority,
            trigger = EXCLUDED.trigger,
            conditions = EXCLUDED.conditions,
            actions = EXCLUDED.actions,
            state = EXCLUDED.state,
            tags = EXCLUDED.tags,
            execution_count = EXCLUDED.execution_count,
            last_executed_at = EXCLUDED.last_executed_at,
            last_error_at = EXCLUDED.last_error_at,
            last_error = EXCLUDED.last_error,
            updated_at = EXCLUDED.updated_at
        """;

    private readonly PostgresConnectionFactory _connectionFactory;
    private readonly ILogger<PostgresRulesService> _logger;

    /// <summary>
    /// Creates a rules service bound to the supplied connection factory.
    /// </summary>
    public PostgresRulesService(PostgresConnectionFactory connectionFactory, ILogger<PostgresRulesService> logger)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        ArgumentNullException.ThrowIfNull(logger);
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<IEnumerable<Rule>> GetAllRulesAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = _connectionFactory.CreateConnection();
        var rows = await conn.QueryAsync<RuleRow>(new CommandDefinition(
            "SELECT * FROM rules ORDER BY priority ASC",
            cancellationToken: cancellationToken));
        return rows.Select(MapToRule).ToList();
    }

    /// <inheritdoc/>
    public async Task<Rule?> GetRuleAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var conn = _connectionFactory.CreateConnection();
        var row = await conn.QuerySingleOrDefaultAsync<RuleRow>(new CommandDefinition(
            "SELECT * FROM rules WHERE id = @Id",
            new { Id = id },
            cancellationToken: cancellationToken));
        return row is null ? null : MapToRule(row);
    }

    /// <inheritdoc/>
    public async Task<Rule> AddOrUpdateRuleAsync(Rule rule, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var now = DateTime.UtcNow;
        rule.UpdatedAt = now;

        var existing = await GetRuleAsync(rule.Id, cancellationToken);
        if (existing is null)
        {
            rule.CreatedAt = now;
            _logger.LogInformation("Adding new rule: {RuleId} - {RuleName}", rule.Id, rule.Name);
        }

        await using var conn = _connectionFactory.CreateConnection();
        await conn.ExecuteAsync(new CommandDefinition(
            UpsertRuleSql,
            new
            {
                rule.Id,
                rule.Name,
                rule.Description,
                rule.Enabled,
                rule.Priority,
                Trigger = JsonSerializer.Serialize(rule.Trigger, JsonOptions),
                Conditions = rule.Conditions is null
                    ? null
                    : JsonSerializer.Serialize(rule.Conditions, JsonOptions),
                Actions = JsonSerializer.Serialize(rule.Actions, JsonOptions),
                State = JsonSerializer.Serialize(rule.State, JsonOptions),
                Tags = JsonSerializer.Serialize(rule.Tags, JsonOptions),
                rule.ExecutionCount,
                rule.LastExecutedAt,
                rule.LastErrorAt,
                rule.LastError,
                rule.CreatedAt,
                rule.UpdatedAt
            },
            cancellationToken: cancellationToken));

        return rule;
    }

    /// <inheritdoc/>
    public async Task<bool> RemoveRuleAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var conn = _connectionFactory.CreateConnection();
        var affected = await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM rules WHERE id = @Id",
            new { Id = id },
            cancellationToken: cancellationToken));

        if (affected <= 0) return false;
        _logger.LogInformation("Removed rule: {RuleId}", id);
        return true;
    }

    /// <inheritdoc/>
    public async Task<bool> EnableRuleAsync(string id, bool enabled, CancellationToken cancellationToken = default)
    {
        await using var conn = _connectionFactory.CreateConnection();
        var affected = await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE rules SET enabled = @Enabled, updated_at = @UpdatedAt WHERE id = @Id",
            new { Id = id, Enabled = enabled, UpdatedAt = DateTime.UtcNow },
            cancellationToken: cancellationToken));

        if (affected <= 0) return false;
        _logger.LogInformation("Rule {RuleId} enabled: {Enabled}", id, enabled);
        return true;
    }

    /// <inheritdoc/>
    public async Task<bool> UpdateRuleStateAsync(string id, Dictionary<string, object> state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        await using var conn = _connectionFactory.CreateConnection();
        var affected = await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE rules SET state = @State::jsonb, updated_at = @UpdatedAt WHERE id = @Id",
            new
            {
                Id = id,
                State = JsonSerializer.Serialize(state, JsonOptions),
                UpdatedAt = DateTime.UtcNow
            },
            cancellationToken: cancellationToken));

        return affected > 0;
    }

    /// <inheritdoc/>
    public async Task<IEnumerable<Rule>> GetMatchingRulesAsync(string topic, CancellationToken cancellationToken = default)
    {
        var allRules = await GetAllRulesAsync(cancellationToken);
        return allRules
            .Where(r => r.Enabled && MqttTopicMatcher.IsMatch(r.Trigger.TopicPattern, topic))
            .OrderBy(r => r.Priority)
            .ToList();
    }

    /// <inheritdoc/>
    public async Task<IEnumerable<Rule>> GetRulesByTagAsync(string tag, CancellationToken cancellationToken = default)
    {
        await using var conn = _connectionFactory.CreateConnection();
        var rows = await conn.QueryAsync<RuleRow>(new CommandDefinition(
            "SELECT * FROM rules WHERE tags @> @Tag::jsonb",
            new { Tag = JsonSerializer.Serialize(new[] { tag }) },
            cancellationToken: cancellationToken));
        return rows.Select(MapToRule).ToList();
    }

    /// <inheritdoc/>
    public async Task<IEnumerable<Rule>> GetRulesByTriggerTypeAsync(TriggerType triggerType, CancellationToken cancellationToken = default)
    {
        await using var conn = _connectionFactory.CreateConnection();
        var rows = await conn.QueryAsync<RuleRow>(new CommandDefinition(
            "SELECT * FROM rules WHERE trigger->>'type' = @Type",
            new { Type = ((int)triggerType).ToString(CultureInfo.InvariantCulture) },
            cancellationToken: cancellationToken));
        return rows.Select(MapToRule).ToList();
    }

    /// <inheritdoc/>
    public async Task<int> CountActiveAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = _connectionFactory.CreateConnection();
        return await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*)::int FROM rules WHERE enabled = TRUE",
            cancellationToken: cancellationToken));
    }

    /// <inheritdoc/>
    public async Task BulkUpdateStatsAsync(
        IReadOnlyCollection<RuleStatsUpdate> updates,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(updates);
        if (updates.Count == 0) return;

        // UPDATE ... FROM (VALUES (...), (...)) runs in one round-trip and
        // touches only the rows named in the batch. COALESCE keeps existing
        // error info when a given update didn't observe a new error.
        var sb = new System.Text.StringBuilder(512);
        sb.Append("""
            UPDATE rules r SET
                execution_count = r.execution_count + v.delta,
                last_executed_at = v.last_executed_at,
                last_error = COALESCE(v.last_error, r.last_error),
                last_error_at = COALESCE(v.last_error_at, r.last_error_at),
                updated_at = NOW()
            FROM (VALUES
            """);

        var parameters = new DynamicParameters();
        var i = 0;
        foreach (var u in updates)
        {
            if (i > 0) sb.Append(',');
            sb.Append(CultureInfo.InvariantCulture, $"(@id{i}, @dc{i}, @lea{i}::timestamptz, @le{i}, @leat{i}::timestamptz)");
            parameters.Add($"id{i}", u.RuleId);
            parameters.Add($"dc{i}", u.DeltaExecutionCount);
            parameters.Add($"lea{i}", u.LastExecutedAt);
            parameters.Add($"le{i}", u.LastError);
            parameters.Add($"leat{i}", u.LastErrorAt);
            i++;
        }
        sb.Append(") AS v(id, delta, last_executed_at, last_error, last_error_at)");
        sb.Append(" WHERE r.id = v.id;");

        await using var conn = _connectionFactory.CreateConnection();
        await conn.ExecuteAsync(new CommandDefinition(
            sb.ToString(), parameters, cancellationToken: cancellationToken));
    }

    private Rule MapToRule(RuleRow row) => new()
    {
        Id = row.Id,
        Name = row.Name,
        Description = row.Description,
        Enabled = row.Enabled,
        Priority = row.Priority,
        Trigger = Deserialize<RuleTrigger>(row.Trigger, $"trigger of rule {row.Id}") ?? new RuleTrigger(),
        Conditions = Deserialize<RuleConditionGroup>(row.Conditions, $"conditions of rule {row.Id}"),
        Actions = Deserialize<List<RuleAction>>(row.Actions, $"actions of rule {row.Id}") ?? new(),
        State = Deserialize<Dictionary<string, object>>(row.State, $"state of rule {row.Id}") ?? new(),
        Tags = Deserialize<List<string>>(row.Tags, $"tags of rule {row.Id}") ?? new(),
        ExecutionCount = row.Execution_Count,
        LastExecutedAt = row.Last_Executed_At,
        LastErrorAt = row.Last_Error_At,
        LastError = row.Last_Error,
        CreatedAt = row.Created_At,
        UpdatedAt = row.Updated_At
    };

    private T? Deserialize<T>(string? json, string field) =>
        DapperJsonColumn.Deserialize<T>(json, JsonOptions, _logger, field);

    private sealed class RuleRow
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public bool Enabled { get; set; }
        public int Priority { get; set; }
        public string? Trigger { get; set; }
        public string? Conditions { get; set; }
        public string? Actions { get; set; }
        public string? State { get; set; }
        public string? Tags { get; set; }
        public int Execution_Count { get; set; }
        public DateTime? Last_Executed_At { get; set; }
        public DateTime? Last_Error_At { get; set; }
        public string? Last_Error { get; set; }
        public DateTime Created_At { get; set; }
        public DateTime Updated_At { get; set; }
    }
}
