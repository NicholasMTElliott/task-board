using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Models;
using TaskBoard.Worker.Processing;
using TaskBoard.Worker.Tests.Helpers;

namespace TaskBoard.Worker.Tests.Stores;

/// <summary>
/// Integration test for the V19 partial UNIQUE index
/// <c>uq_step_result_candidate_slot</c>. The index guards
/// <c>(tenant_id, candidate_group_id, candidate_index)</c> for candidate rows
/// only. Without it, a runtime bug that retried a candidate save with a
/// different <c>step_name</c> (bypassing the V17 ON CONFLICT clause keyed on
/// run_id+step_name) would silently produce two rows for the same candidate
/// slot and double-count in <c>v_provider_role_metrics</c>. With it, the
/// duplicate fails loudly at the DB.
/// </summary>
/// <remarks>
/// Connects to the local Postgres instance from <c>docker-compose.yml</c> by
/// default. Override via <c>TASKBOARD_TEST_PG_CONNECTION</c>. Skipped (not
/// failed) when the database is unreachable so the rest of the suite still
/// runs in environments without docker compose up.
/// </remarks>
public class PgRunStoreCandidateUniqueIndexTests
{
    private const string DefaultConnectionString =
        "Host=localhost;Port=5432;Database=aiboard;Username=aiboard;Password=aiboard;Include Error Detail=true";

    private static string ConnectionString =>
        Environment.GetEnvironmentVariable("TASKBOARD_TEST_PG_CONNECTION") ?? DefaultConnectionString;

    private static async Task<NpgsqlDataSource?> TryConnectAsync()
    {
        try
        {
            var dataSource = NpgsqlDataSource.Create(ConnectionString);
            await using var conn = await dataSource.OpenConnectionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1";
            await cmd.ExecuteScalarAsync();
            return dataSource;
        }
        catch
        {
            return null;
        }
    }

    [Fact]
    public async Task DuplicateCandidateSlot_FailsWithUniqueViolation()
    {
        var dataSource = await TryConnectAsync();
        if (dataSource is null)
        {
            // Skip — environment doesn't have a reachable Postgres. The whole
            // point of this test is the live DB constraint, so a fake-DB fallback
            // would defeat its purpose.
            return;
        }

        await using (dataSource)
        {
            var tenant = new TestTenant();
            var store = new PgRunStore(dataSource, tenant, NullLogger<PgRunStore>.Instance);

            // Seed an agent_run so the FK on step_result holds. Run id includes
            // a fresh GUID so concurrent test runs don't trip over each other.
            var runId = $"v19-test-{Guid.NewGuid():N}";
            var cardId = $"v19-card-{Guid.NewGuid():N}";
            var groupId = Guid.NewGuid();

            await store.CreateRunAsync(new RunRecord(
                RunId: runId,
                CardId: cardId,
                StateName: "test_state",
                AgentIdentity: "v19-test",
                GitBranch: null,
                TotalSteps: 1,
                StartedAtUtc: DateTimeOffset.UtcNow), CancellationToken.None);

            try
            {
                // First candidate row — succeeds.
                var first = MakeRow(runId, cardId, groupId, candidateIndex: 0,
                    stepName: "step:cand-0:claude-cli");
                await store.SaveStepResultAsync(first, CancellationToken.None);

                // Second row with the SAME (tenant_id, candidate_group_id,
                // candidate_index) but a DIFFERENT step_name. The
                // step_name-keyed ON CONFLICT in SaveStepResult does not catch
                // this; the V19 partial UNIQUE index must.
                var duplicate = MakeRow(runId, cardId, groupId, candidateIndex: 0,
                    stepName: "step:cand-0:claude-cli:retry");

                var ex = await Assert.ThrowsAsync<PostgresException>(() =>
                    store.SaveStepResultAsync(duplicate, CancellationToken.None));

                // 23505 = unique_violation. The index name is the explicit
                // signal that V19 is what fired (vs. some other unique constraint
                // we might add later).
                Assert.Equal("23505", ex.SqlState);
                Assert.Contains("uq_step_result_candidate_slot", ex.Message,
                    StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                // Best-effort cleanup. Test rows are uniquely keyed so leftovers
                // don't poison subsequent runs, but tidiness wins.
                await using var conn = await dataSource.OpenConnectionAsync();
                await using var del = conn.CreateCommand();
                del.CommandText = "DELETE FROM agent_run WHERE tenant_id = $1 AND run_id = $2";
                del.Parameters.AddWithValue(tenant.Value);
                del.Parameters.AddWithValue(runId);
                try { await del.ExecuteNonQueryAsync(); } catch { /* ignore */ }
            }
        }
    }

    [Fact]
    public async Task NonCandidateRows_AreNotAffectedByUniqueIndex()
    {
        // The V19 index is partial (WHERE candidate_group_id IS NOT NULL), so
        // traditional single-agent steps (candidate_group_id NULL) must remain
        // free to share whatever (run_id, step_name) constraints the table
        // already has. Two non-candidate rows with the same step_name still hit
        // the V17 ON CONFLICT path, but we want to prove the V19 index isn't
        // somehow broadening the constraint to NULL rows.
        var dataSource = await TryConnectAsync();
        if (dataSource is null) return;

        await using (dataSource)
        {
            var tenant = new TestTenant();
            var store = new PgRunStore(dataSource, tenant, NullLogger<PgRunStore>.Instance);

            var runId = $"v19-nonc-{Guid.NewGuid():N}";
            var cardId = $"v19-card-{Guid.NewGuid():N}";

            await store.CreateRunAsync(new RunRecord(
                RunId: runId, CardId: cardId, StateName: "test_state",
                AgentIdentity: "v19-test", GitBranch: null, TotalSteps: 2,
                StartedAtUtc: DateTimeOffset.UtcNow), CancellationToken.None);

            try
            {
                var rowA = MakeRow(runId, cardId, groupId: null, candidateIndex: null,
                    stepName: "step-a");
                var rowB = MakeRow(runId, cardId, groupId: null, candidateIndex: null,
                    stepName: "step-b");

                // Both succeed: distinct step_names, no candidate-group constraint applies.
                await store.SaveStepResultAsync(rowA, CancellationToken.None);
                await store.SaveStepResultAsync(rowB, CancellationToken.None);
            }
            finally
            {
                await using var conn = await dataSource.OpenConnectionAsync();
                await using var del = conn.CreateCommand();
                del.CommandText = "DELETE FROM agent_run WHERE tenant_id = $1 AND run_id = $2";
                del.Parameters.AddWithValue(tenant.Value);
                del.Parameters.AddWithValue(runId);
                try { await del.ExecuteNonQueryAsync(); } catch { /* ignore */ }
            }
        }
    }

    private static StepResultRecord MakeRow(
        string runId, string cardId, Guid? groupId, int? candidateIndex, string stepName)
        => new(
            RunId: runId,
            CardId: cardId,
            StateName: "test_state",
            StepName: stepName,
            StepIndex: 0,
            Role: "implementer",
            Model: "claude-sonnet-4-6",
            Outcome: AgentOutcome.COMPLETE,
            Summary: null,
            Detail: null,
            ReferenceContent: null,
            ConversationLog: null,
            Questions: null,
            RequestedSteps: null,
            StartedAtUtc: DateTimeOffset.UtcNow,
            CompletedAtUtc: DateTimeOffset.UtcNow,
            SessionExecMs: null,
            Provider: "claude-cli",
            CandidateGroupId: groupId,
            CandidateIndex: candidateIndex,
            Selected: null,
            QualityScore: null,
            EvaluatorReasoning: null);
}
