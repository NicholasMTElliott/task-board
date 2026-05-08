-- V25: Rerun-redesign Round-5 Finding 3 — migrate v_fast_path_hit_rate from
-- the deprecated fast_path_hit column to execution_kind = 'cache_hit'.
--
-- The fast_path_hit column was populated by the (now-removed) RerunPreambleBuilder
-- when an LLM-judged "bail if unchanged" preamble was injected and the agent
-- responded COMPLETE without redoing work. The deterministic cache gate that
-- replaced the preamble records its decisions in execution_kind ('cache_hit'
-- vs 'full_run'), which is the same operator question ("how often did we skip
-- this step?") expressed via persisted state instead of LLM judgment.
--
-- The new view name v_cache_hit_rate matches the column it reads from. The old
-- view is dropped — its semantics ("preamble fired and agent agreed") no longer
-- exist in the runtime. Operators querying v_fast_path_hit_rate will see a
-- clear DB-level error rather than silently zero results.

DROP VIEW IF EXISTS v_fast_path_hit_rate;

CREATE VIEW v_cache_hit_rate AS
SELECT
    tenant_id,
    state_name,
    step_name,
    role,
    provider,
    COUNT(*)                                                AS total_invocations,
    COUNT(*) FILTER (WHERE execution_kind = 'cache_hit')    AS cache_hits,
    CASE
        WHEN COUNT(*) = 0 THEN NULL
        ELSE 100.0 *
            COUNT(*) FILTER (WHERE execution_kind = 'cache_hit') /
            COUNT(*)
    END                                                     AS hit_rate_percent
FROM step_result
WHERE completed_at_utc IS NOT NULL
  AND execution_kind IN ('full_run', 'cache_hit')
GROUP BY tenant_id, state_name, step_name, role, provider;
