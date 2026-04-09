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
            INSERT INTO agent_run (run_id, card_id, state_name, agent_identity, git_branch, total_steps, started_at_utc)
            VALUES ($1, $2, $3, $4, $5, $6, $7)
            """;
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
        cmd.CommandText = "UPDATE agent_run SET completed_steps = $2 WHERE run_id = $1";
        cmd.Parameters.AddWithValue(runId);
        cmd.Parameters.AddWithValue(completedSteps);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task CompleteRunAsync(string runId, AgentOutcome outcome, string? errorDetail, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE agent_run SET outcome = $2, error_detail = $3, completed_at_utc = NOW() WHERE run_id = $1
            """;
        cmd.Parameters.AddWithValue(runId);
        cmd.Parameters.AddWithValue(outcome.ToString());
        cmd.Parameters.AddWithValue(errorDetail is null ? DBNull.Value : (object)errorDetail);
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
                (run_id, card_id, state_name, step_name, step_index, role, model,
                 outcome, summary, detail, reference_content, conversation_log,
                 questions, requested_steps, started_at_utc, completed_at_utc, session_exec_ms)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13::jsonb, $14::jsonb, $15, $16, $17)
            ON CONFLICT (run_id, step_name) DO NOTHING
            """;
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

        await cmd.ExecuteNonQueryAsync(ct);

        logger.LogDebug("Saved step_result for run {RunId} step '{StepName}'", result.RunId, result.StepName);
    }

    public async Task<IReadOnlyList<StepResultRecord>> GetStepResultsForCardAsync(
        string cardId, string? stateName, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, run_id, card_id, state_name, step_name, step_index, role, model,
                   outcome, summary, detail, reference_content, conversation_log,
                   questions, requested_steps, started_at_utc, completed_at_utc, session_exec_ms
            FROM step_result
            WHERE card_id = $1
              AND ($2::text IS NULL OR state_name = $2)
            ORDER BY completed_at_utc DESC
            """;
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
                   sr.questions, sr.requested_steps, sr.started_at_utc, sr.completed_at_utc, sr.session_exec_ms
            FROM step_result sr
            INNER JOIN (
                SELECT run_id FROM agent_run
                WHERE card_id = $1 AND state_name = $2
                ORDER BY started_at_utc DESC LIMIT 1
            ) ar ON sr.run_id = ar.run_id
            ORDER BY sr.step_index
            """;
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
                SessionExecMs: reader.IsDBNull(reader.GetOrdinal("session_exec_ms")) ? null : reader.GetInt32(reader.GetOrdinal("session_exec_ms"))));
        }
        return results;
    }

    public async Task UpdateRunEstimateAsync(string runId, double estimate, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE agent_run SET estimate = $2 WHERE run_id = $1";
        cmd.Parameters.AddWithValue(runId);
        cmd.Parameters.AddWithValue(estimate);
        await cmd.ExecuteNonQueryAsync(ct);

        logger.LogDebug("Updated estimate for run {RunId}: {Estimate}", runId, estimate);
    }

    public async Task UpdateRunSessionStartupMsAsync(string runId, int startupMs, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE agent_run SET session_startup_ms = $2 WHERE run_id = $1";
        cmd.Parameters.AddWithValue(runId);
        cmd.Parameters.AddWithValue(startupMs);
        await cmd.ExecuteNonQueryAsync(ct);

        logger.LogDebug("Updated session_startup_ms for run {RunId}: {StartupMs}ms", runId, startupMs);
    }
}
