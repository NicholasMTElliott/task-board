using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Models;

namespace TaskBoard.Worker.Processing;

/// <summary>
/// PostgreSQL implementation of IRunStore using raw Npgsql queries.
/// Follows the CardClaimService pattern: NpgsqlDataSource, parameterized queries, connection-per-call.
/// </summary>
public sealed class PgRunStore(
    NpgsqlDataSource dataSource,
    ITenantIdentifier tenant,
    ILogger<PgRunStore> logger) : IRunStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public async Task CreateRunAsync(RunRecord run, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO agent_run (tenant_id, run_id, card_id, state_name, agent_identity, git_branch, total_steps, started_at_utc)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8)
            """;
        cmd.Parameters.AddWithValue(tenant.Value);
        cmd.Parameters.AddWithValue(run.RunId);
        cmd.Parameters.AddWithValue(run.CardId);
        cmd.Parameters.AddWithValue(run.StateName);
        cmd.Parameters.AddWithValue(run.AgentIdentity);
        cmd.Parameters.AddWithValue(run.GitBranch is null ? DBNull.Value : (object)run.GitBranch);
        cmd.Parameters.AddWithValue(run.TotalSteps);
        cmd.Parameters.AddWithValue(run.StartedAtUtc ?? (object)DateTimeOffset.UtcNow);
        await cmd.ExecuteNonQueryAsync(ct);

        logger.LogDebug("Created agent_run {RunId} for card {CardId}", run.RunId, run.CardId);
    }

    public async Task UpdateRunProgressAsync(string runId, int completedSteps, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE agent_run SET completed_steps = $3 WHERE tenant_id = $1 AND run_id = $2";
        cmd.Parameters.AddWithValue(tenant.Value);
        cmd.Parameters.AddWithValue(runId);
        cmd.Parameters.AddWithValue(completedSteps);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task CompleteRunAsync(string runId, AgentOutcome outcome, string? errorDetail, FailureReason? failureReason, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE agent_run SET outcome = $3, error_detail = $4, failure_reason = $5, completed_at_utc = NOW()
            WHERE tenant_id = $1 AND run_id = $2
            """;
        cmd.Parameters.AddWithValue(tenant.Value);
        cmd.Parameters.AddWithValue(runId);
        cmd.Parameters.AddWithValue(outcome.ToString());
        cmd.Parameters.AddWithValue(errorDetail is null ? DBNull.Value : (object)errorDetail);
        cmd.Parameters.AddWithValue(failureReason is null ? DBNull.Value : (object)failureReason.Value.ToString());
        await cmd.ExecuteNonQueryAsync(ct);

        logger.LogDebug("Completed agent_run {RunId} with outcome {Outcome}", runId, outcome);
    }

    public async Task SaveStepResultAsync(StepResultRecord result, CancellationToken ct)
    {
        var questionsJson = result.Questions is { Count: > 0 }
            ? JsonSerializer.Serialize(result.Questions, JsonOptions)
            : null;
        var requestedStepsJson = result.RequestedSteps is { Count: > 0 }
            ? JsonSerializer.Serialize(result.RequestedSteps, JsonOptions)
            : null;

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO step_result
                (tenant_id, run_id, card_id, state_name, step_name, step_index, role, model,
                 outcome, summary, detail, reference_content, conversation_log,
                 questions, requested_steps, started_at_utc, completed_at_utc, session_exec_ms,
                 provider, candidate_group_id, candidate_index,
                 selected, quality_score, evaluator_reasoning, slot_index,
                 cost_usd, input_tokens, output_tokens, cache_read_tokens, cache_creation_tokens,
                 fast_path_hit, structurer_fallback_used, evaluator_prompt_chars, winner_regressed)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14::jsonb, $15::jsonb, $16, $17, $18,
                    $19, $20, $21, $22, $23, $24, $25,
                    $26, $27, $28, $29, $30,
                    $31, $32, $33, $34)
            ON CONFLICT (tenant_id, run_id, step_name) DO NOTHING
            """;
        cmd.Parameters.AddWithValue(tenant.Value);
        cmd.Parameters.AddWithValue(result.RunId);
        cmd.Parameters.AddWithValue(result.CardId);
        cmd.Parameters.AddWithValue(result.StateName);
        cmd.Parameters.AddWithValue(result.StepName);
        cmd.Parameters.AddWithValue(result.StepIndex);
        cmd.Parameters.AddWithValue(result.Role);
        cmd.Parameters.AddWithValue(result.Model);
        cmd.Parameters.AddWithValue(result.Outcome.ToString());
        cmd.Parameters.AddWithValue(result.Summary is null ? DBNull.Value : (object)result.Summary);
        cmd.Parameters.AddWithValue(result.Detail is null ? DBNull.Value : (object)result.Detail);
        cmd.Parameters.AddWithValue(result.ReferenceContent is null ? DBNull.Value : (object)result.ReferenceContent);
        cmd.Parameters.AddWithValue(result.ConversationLog is null ? DBNull.Value : (object)result.ConversationLog);

        var questionsParam = cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb });
        questionsParam.Value = questionsJson is null ? DBNull.Value : (object)questionsJson;

        var requestedStepsParam = cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb });
        requestedStepsParam.Value = requestedStepsJson is null ? DBNull.Value : (object)requestedStepsJson;

        cmd.Parameters.AddWithValue(result.StartedAtUtc);
        cmd.Parameters.AddWithValue(result.CompletedAtUtc);
        cmd.Parameters.AddWithValue(result.SessionExecMs.HasValue ? (object)result.SessionExecMs.Value : DBNull.Value);

        // V17 candidate-group metadata (provider always populated; the rest are
        // null for traditional non-candidate steps).
        cmd.Parameters.AddWithValue(result.Provider);
        cmd.Parameters.AddWithValue(result.CandidateGroupId.HasValue ? (object)result.CandidateGroupId.Value : DBNull.Value);
        cmd.Parameters.AddWithValue(result.CandidateIndex.HasValue ? (object)result.CandidateIndex.Value : DBNull.Value);
        cmd.Parameters.AddWithValue(result.Selected.HasValue ? (object)result.Selected.Value : DBNull.Value);
        cmd.Parameters.AddWithValue(result.QualityScore.HasValue ? (object)result.QualityScore.Value : DBNull.Value);
        cmd.Parameters.AddWithValue(result.EvaluatorReasoning is null ? DBNull.Value : (object)result.EvaluatorReasoning);
        // V20 slot_index: null for single-slot steps (legacy + new 1-slot configs)
        // and for non-candidate rows; populated for multi-slot fallback chains.
        cmd.Parameters.AddWithValue(result.SlotIndex.HasValue ? (object)result.SlotIndex.Value : DBNull.Value);

        // V22 usage / fast-path / structurer / evaluator-prompt-size / regression columns.
        cmd.Parameters.AddWithValue(result.CostUsd.HasValue ? (object)result.CostUsd.Value : DBNull.Value);
        cmd.Parameters.AddWithValue(result.InputTokens.HasValue ? (object)result.InputTokens.Value : DBNull.Value);
        cmd.Parameters.AddWithValue(result.OutputTokens.HasValue ? (object)result.OutputTokens.Value : DBNull.Value);
        cmd.Parameters.AddWithValue(result.CacheReadTokens.HasValue ? (object)result.CacheReadTokens.Value : DBNull.Value);
        cmd.Parameters.AddWithValue(result.CacheCreationTokens.HasValue ? (object)result.CacheCreationTokens.Value : DBNull.Value);
        cmd.Parameters.AddWithValue(result.FastPathHit.HasValue ? (object)result.FastPathHit.Value : DBNull.Value);
        cmd.Parameters.AddWithValue(result.StructurerFallbackUsed.HasValue ? (object)result.StructurerFallbackUsed.Value : DBNull.Value);
        cmd.Parameters.AddWithValue(result.EvaluatorPromptChars.HasValue ? (object)result.EvaluatorPromptChars.Value : DBNull.Value);
        cmd.Parameters.AddWithValue(result.WinnerRegressed.HasValue ? (object)result.WinnerRegressed.Value : DBNull.Value);

        await cmd.ExecuteNonQueryAsync(ct);

        logger.LogDebug("Saved step_result for run {RunId} step '{StepName}'", result.RunId, result.StepName);
    }

    public async Task UpdateCandidateEvaluationAsync(
        string runId,
        Guid candidateGroupId,
        int candidateIndex,
        bool selected,
        decimal? qualityScore,
        string? evaluatorReasoning,
        CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE step_result
            SET selected = $4,
                quality_score = $5,
                evaluator_reasoning = $6
            WHERE tenant_id = $1
              AND run_id = $2
              AND candidate_group_id = $3
              AND candidate_index = $7
            """;
        cmd.Parameters.AddWithValue(tenant.Value);
        cmd.Parameters.AddWithValue(runId);
        cmd.Parameters.AddWithValue(candidateGroupId);
        cmd.Parameters.AddWithValue(selected);
        cmd.Parameters.AddWithValue(qualityScore.HasValue ? (object)qualityScore.Value : DBNull.Value);
        cmd.Parameters.AddWithValue(evaluatorReasoning is null ? DBNull.Value : (object)evaluatorReasoning);
        cmd.Parameters.AddWithValue(candidateIndex);
        await cmd.ExecuteNonQueryAsync(ct);

        logger.LogDebug(
            "Updated candidate evaluation for run {RunId} group {Group} index {Index}: selected={Selected}, score={Score}",
            runId, candidateGroupId, candidateIndex, selected, qualityScore);
    }

    public async Task<IReadOnlyList<StepResultRecord>> GetStepResultsForCardAsync(
        string cardId, string? stateName, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, run_id, card_id, state_name, step_name, step_index, role, model,
                   outcome, summary, detail, reference_content, conversation_log,
                   questions, requested_steps, started_at_utc, completed_at_utc, session_exec_ms,
                   provider, candidate_group_id, candidate_index,
                   selected, quality_score, evaluator_reasoning, slot_index,
                   cost_usd, input_tokens, output_tokens, cache_read_tokens, cache_creation_tokens,
                   fast_path_hit, structurer_fallback_used, evaluator_prompt_chars, winner_regressed
            FROM step_result
            WHERE tenant_id = $1
              AND card_id = $2
              AND ($3::text IS NULL OR state_name = $3)
            ORDER BY completed_at_utc DESC
            """;
        cmd.Parameters.AddWithValue(tenant.Value);
        cmd.Parameters.AddWithValue(cardId);
        cmd.Parameters.AddWithValue(stateName is null ? DBNull.Value : (object)stateName);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await ReadStepResultsAsync(reader, ct);
    }

    public async Task<IReadOnlyList<StepResultRecord>> GetLatestRunStepResultsAsync(
        string cardId, string stateName, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT sr.id, sr.run_id, sr.card_id, sr.state_name, sr.step_name, sr.step_index, sr.role, sr.model,
                   sr.outcome, sr.summary, sr.detail, sr.reference_content, sr.conversation_log,
                   sr.questions, sr.requested_steps, sr.started_at_utc, sr.completed_at_utc, sr.session_exec_ms,
                   sr.provider, sr.candidate_group_id, sr.candidate_index,
                   sr.selected, sr.quality_score, sr.evaluator_reasoning, sr.slot_index,
                   sr.cost_usd, sr.input_tokens, sr.output_tokens, sr.cache_read_tokens, sr.cache_creation_tokens,
                   sr.fast_path_hit, sr.structurer_fallback_used, sr.evaluator_prompt_chars, sr.winner_regressed
            FROM step_result sr
            INNER JOIN (
                SELECT run_id FROM agent_run
                WHERE tenant_id = $1 AND card_id = $2 AND state_name = $3
                ORDER BY started_at_utc DESC LIMIT 1
            ) ar ON sr.run_id = ar.run_id
            WHERE sr.tenant_id = $1
            ORDER BY sr.step_index
            """;
        cmd.Parameters.AddWithValue(tenant.Value);
        cmd.Parameters.AddWithValue(cardId);
        cmd.Parameters.AddWithValue(stateName);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await ReadStepResultsAsync(reader, ct);
    }

    private static async Task<List<StepResultRecord>> ReadStepResultsAsync(
        NpgsqlDataReader reader, CancellationToken ct)
    {
        var results = new List<StepResultRecord>();
        while (await reader.ReadAsync(ct))
        {
            var questions = reader.IsDBNull(reader.GetOrdinal("questions"))
                ? null
                : JsonSerializer.Deserialize<List<AgentQuestion>>(
                    reader.GetString(reader.GetOrdinal("questions")), JsonOptions);

            var requestedSteps = reader.IsDBNull(reader.GetOrdinal("requested_steps"))
                ? null
                : JsonSerializer.Deserialize<List<string>>(
                    reader.GetString(reader.GetOrdinal("requested_steps")), JsonOptions);

            var outcome = Enum.Parse<AgentOutcome>(reader.GetString(reader.GetOrdinal("outcome")));

            results.Add(new StepResultRecord(
                RunId: reader.GetString(reader.GetOrdinal("run_id")),
                CardId: reader.GetString(reader.GetOrdinal("card_id")),
                StateName: reader.GetString(reader.GetOrdinal("state_name")),
                StepName: reader.GetString(reader.GetOrdinal("step_name")),
                StepIndex: reader.GetInt32(reader.GetOrdinal("step_index")),
                Role: reader.GetString(reader.GetOrdinal("role")),
                Model: reader.GetString(reader.GetOrdinal("model")),
                Outcome: outcome,
                Summary: reader.IsDBNull(reader.GetOrdinal("summary")) ? null : reader.GetString(reader.GetOrdinal("summary")),
                Detail: reader.IsDBNull(reader.GetOrdinal("detail")) ? null : reader.GetString(reader.GetOrdinal("detail")),
                ReferenceContent: reader.IsDBNull(reader.GetOrdinal("reference_content")) ? null : reader.GetString(reader.GetOrdinal("reference_content")),
                ConversationLog: reader.IsDBNull(reader.GetOrdinal("conversation_log")) ? null : reader.GetString(reader.GetOrdinal("conversation_log")),
                Questions: questions,
                RequestedSteps: requestedSteps,
                StartedAtUtc: reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("started_at_utc")),
                CompletedAtUtc: reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("completed_at_utc")),
                SessionExecMs: reader.IsDBNull(reader.GetOrdinal("session_exec_ms")) ? null : reader.GetInt32(reader.GetOrdinal("session_exec_ms")),
                Provider: reader.GetString(reader.GetOrdinal("provider")),
                CandidateGroupId: reader.IsDBNull(reader.GetOrdinal("candidate_group_id")) ? null : reader.GetGuid(reader.GetOrdinal("candidate_group_id")),
                CandidateIndex: reader.IsDBNull(reader.GetOrdinal("candidate_index")) ? null : reader.GetInt32(reader.GetOrdinal("candidate_index")),
                Selected: reader.IsDBNull(reader.GetOrdinal("selected")) ? null : reader.GetBoolean(reader.GetOrdinal("selected")),
                QualityScore: reader.IsDBNull(reader.GetOrdinal("quality_score")) ? null : reader.GetDecimal(reader.GetOrdinal("quality_score")),
                EvaluatorReasoning: reader.IsDBNull(reader.GetOrdinal("evaluator_reasoning")) ? null : reader.GetString(reader.GetOrdinal("evaluator_reasoning")),
                SlotIndex: ReadNullableInt(reader, "slot_index"),
                CostUsd: ReadNullableDecimal(reader, "cost_usd"),
                InputTokens: ReadNullableLong(reader, "input_tokens"),
                OutputTokens: ReadNullableLong(reader, "output_tokens"),
                CacheReadTokens: ReadNullableLong(reader, "cache_read_tokens"),
                CacheCreationTokens: ReadNullableLong(reader, "cache_creation_tokens"),
                FastPathHit: ReadNullableBool(reader, "fast_path_hit"),
                StructurerFallbackUsed: ReadNullableBool(reader, "structurer_fallback_used"),
                EvaluatorPromptChars: ReadNullableInt(reader, "evaluator_prompt_chars"),
                WinnerRegressed: ReadNullableBool(reader, "winner_regressed")));
        }
        return results;
    }

    private static int? ReadNullableInt(NpgsqlDataReader r, string col)
    {
        var ord = r.GetOrdinal(col);
        return r.IsDBNull(ord) ? null : r.GetInt32(ord);
    }
    private static long? ReadNullableLong(NpgsqlDataReader r, string col)
    {
        var ord = r.GetOrdinal(col);
        return r.IsDBNull(ord) ? null : r.GetInt64(ord);
    }
    private static decimal? ReadNullableDecimal(NpgsqlDataReader r, string col)
    {
        var ord = r.GetOrdinal(col);
        return r.IsDBNull(ord) ? null : r.GetDecimal(ord);
    }
    private static bool? ReadNullableBool(NpgsqlDataReader r, string col)
    {
        var ord = r.GetOrdinal(col);
        return r.IsDBNull(ord) ? null : r.GetBoolean(ord);
    }

    public async Task UpdateRunEstimateAsync(string runId, double estimate, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE agent_run SET estimate = $3 WHERE tenant_id = $1 AND run_id = $2";
        cmd.Parameters.AddWithValue(tenant.Value);
        cmd.Parameters.AddWithValue(runId);
        cmd.Parameters.AddWithValue(estimate);
        await cmd.ExecuteNonQueryAsync(ct);

        logger.LogDebug("Updated estimate for run {RunId}: {Estimate}", runId, estimate);
    }

    public async Task UpdateRunSessionStartupMsAsync(string runId, int startupMs, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE agent_run SET session_startup_ms = $3 WHERE tenant_id = $1 AND run_id = $2";
        cmd.Parameters.AddWithValue(tenant.Value);
        cmd.Parameters.AddWithValue(runId);
        cmd.Parameters.AddWithValue(startupMs);
        await cmd.ExecuteNonQueryAsync(ct);

        logger.LogDebug("Updated session_startup_ms for run {RunId}: {StartupMs}ms", runId, startupMs);
    }

    public async Task IncrementRateLimitEventsAsync(string runId, CancellationToken ct)
    {
        try
        {
            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE agent_run SET rate_limit_events = rate_limit_events + 1
                WHERE tenant_id = $1 AND run_id = $2
                """;
            cmd.Parameters.AddWithValue(tenant.Value);
            cmd.Parameters.AddWithValue(runId);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex)
        {
            // Counter is observability, not behaviour-critical. Don't break the
            // rate-limit recovery path on a DB hiccup.
            logger.LogWarning(ex,
                "Failed to increment rate_limit_events for run {RunId}; continuing.", runId);
        }
    }

    public async Task FlagWinnersRegressedForRunAsync(string runId, CancellationToken ct)
    {
        try
        {
            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE step_result
                SET winner_regressed = true
                WHERE tenant_id = $1
                  AND run_id = $2
                  AND selected = true
                  AND candidate_group_id IS NOT NULL
                """;
            cmd.Parameters.AddWithValue(tenant.Value);
            cmd.Parameters.AddWithValue(runId);
            var updated = await cmd.ExecuteNonQueryAsync(ct);
            logger.LogInformation(
                "Flagged {Count} candidate winner(s) as winner_regressed for run {RunId}",
                updated, runId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to flag winners as regressed for run {RunId}; continuing.", runId);
        }
    }
}
