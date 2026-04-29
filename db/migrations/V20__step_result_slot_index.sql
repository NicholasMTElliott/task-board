-- V20: Add slot_index column to step_result for multi-slot fallback chains.
--
-- A workflow step now has an ordered list of "slots" (parallel candidate
-- groups). Slot 0 runs first; if it fails (no winner picked / evaluator
-- error), slot 1 fires as fallback; etc. Within a slot the existing
-- candidate_group_id / candidate_index columns disambiguate parallel
-- candidates. slot_index disambiguates which slot inside a multi-slot step
-- a row belongs to.
--
-- Nullable by design: legacy single-slot steps (and the new shape's
-- 1-slot configurations) leave slot_index NULL. Migration is therefore
-- non-destructive — every row pre-dating V20 keeps slot_index = NULL,
-- which is the correct value for "this row was not part of a multi-slot
-- fallback chain."
ALTER TABLE step_result
    ADD COLUMN slot_index INT NULL;

COMMENT ON COLUMN step_result.slot_index IS
    'Position of this row''s slot within a multi-slot step (0..N-1). NULL when the step had only one slot or for non-candidate rows. Disambiguates failed-fallback rows from winning-slot rows when joining for metrics.';
