using System.Text.Json;
using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Models;

namespace TaskBoard.Worker.Processing;

/// <summary>
/// Deterministic-skip cache decision (rerun redesign Problem 1). Decides
/// whether a step can be skipped based on a hash comparison: if the prior
/// run's input bundle is byte-identical to this run's, AND the prior
/// section's output is still in the card body, skip the agent invocation
/// and reuse the prior <c>step_result</c>. Replaced the previous
/// LLM-judgment fast-path entirely.
///
/// <para>
/// This service is wired through DI and consulted by <see cref="AgentRunner"/>
/// at the head of every step. It computes the current input hash regardless
/// of cache availability so the hash can be persisted on the step_result
/// row whether the cache hit or missed — populating it makes future runs'
/// cache decisions cheap.
/// </para>
/// </summary>
public sealed class RerunCacheGate(
    IRunStore runStore,
    ILogger<RerunCacheGate> logger)
{
    /// <summary>
    /// Evaluates the cache decision for a step. Always returns the current
    /// input hash (so the caller can persist it); the <see cref="CacheGateResult.IsHit"/>
    /// flag indicates whether the agent invocation can be skipped.
    /// </summary>
    /// <param name="cardBody">Current card body (snapshot used for the hash).</param>
    /// <param name="state">The workflow state being executed.</param>
    /// <param name="step">The step within the state.</param>
    /// <param name="role">The role config for the step.</param>
    /// <param name="stepConfigJson">Serialized step + role config — included in the input bundle so changes invalidate the cache.</param>
    /// <param name="systemPromptContents">Contents of the role's system prompt file at this run.</param>
    /// <param name="taskPromptContents">Contents of the step's task prompt at this run.</param>
    /// <param name="existingComments">All comments currently on the card (operator + agent — the gate filters internally).</param>
    /// <param name="priorSectionOutputHashes">Section hashes of upstream steps' sections, in visitation order.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<CacheGateResult> EvaluateAsync(
        string cardId,
        string cardBody,
        WorkflowState state,
        WorkflowStep step,
        WorkflowRole role,
        string stepConfigJson,
        string systemPromptContents,
        string taskPromptContents,
        IReadOnlyList<CardComment> existingComments,
        IReadOnlyList<string> priorSectionOutputHashes,
        CancellationToken ct)
    {
        // Build the input bundle and compute the current hash.
        var operatorPrefix = ExtractOperatorAuthoredPortion(cardBody);
        var operatorComments = existingComments
            .Where(c => !TaskFileManager.ContainsAgentMarker(c.Body))
            .Select(c => c.Body)
            .ToList();

        var inputs = new InputHashInputs(
            OperatorAuthoredDescription: operatorPrefix,
            OperatorComments: operatorComments,
            PriorSectionOutputHashes: priorSectionOutputHashes,
            StepConfigJson: stepConfigJson,
            SystemPromptContents: systemPromptContents,
            TaskPromptContents: taskPromptContents);

        var currentInputHash = RerunHashBuilder.ComputeInputHash(inputs);
        var currentSectionHash = RerunHashBuilder.ComputeSectionHash(
            cardBody, step.Name, out var sectionDiagnostic);

        // Look up prior COMPLETE.
        var prior = await runStore.GetMostRecentCompleteForStepAsync(
            cardId, state.Name, step.Name, ct);
        if (prior is null)
        {
            logger.LogDebug(
                "Cache miss for step '{Step}' on card {Card}: no prior COMPLETE",
                step.Name, cardId);
            return new CacheGateResult(IsHit: false, currentInputHash, currentSectionHash, Source: null, Diagnostic: sectionDiagnostic);
        }

        // Hash mismatch → miss. Common cases: operator edited description, an
        // upstream step's section_output_hash changed, the workflow config
        // changed.
        if (!StringEquals(prior.InputHash, currentInputHash))
        {
            logger.LogInformation(
                "Cache miss for step '{Step}' on card {Card}: input_hash differs (prior={Prior}, current={Current})",
                step.Name, cardId,
                ShortHash(prior.InputHash), ShortHash(currentInputHash));
            return new CacheGateResult(IsHit: false, currentInputHash, currentSectionHash, Source: null, Diagnostic: sectionDiagnostic);
        }

        // Section drift → miss. The step's own output section was edited
        // externally between runs; the agent must re-evaluate against the
        // current state of its own truth.
        if (!StringEquals(prior.SectionOutputHash, currentSectionHash))
        {
            logger.LogInformation(
                "Cache miss for step '{Step}' on card {Card}: section_output_hash drifted (prior={Prior}, current={Current})",
                step.Name, cardId,
                ShortHash(prior.SectionOutputHash), ShortHash(currentSectionHash));
            return new CacheGateResult(IsHit: false, currentInputHash, currentSectionHash, Source: null, Diagnostic: sectionDiagnostic);
        }

        // Hit.
        logger.LogInformation(
            "Cache HIT for step '{Step}' on card {Card}: reusing run {SourceRun} (completed {Completed})",
            step.Name, cardId, prior.RunId, prior.CompletedAtUtc);
        return new CacheGateResult(IsHit: true, currentInputHash, currentSectionHash, Source: prior, Diagnostic: sectionDiagnostic);
    }

    /// <summary>
    /// Builds the JSON serialization of a step + role + state-level provider
    /// params for the input hash. Comprehensive enough that any meaningful
    /// change to the agent's approach invalidates the cache: provider,
    /// model, state-level providerParams, candidate / slot config, retry
    /// policy, prompt file references.
    /// </summary>
    public static string SerializeStepConfig(
        WorkflowStep step, WorkflowRole role, IReadOnlyDictionary<string, string>? stateProviderParams)
    {
        var bundle = new
        {
            step = new
            {
                name = step.Name,
                role = step.Role,
                taskPrompt = step.TaskPrompt,
                taskPromptFile = step.TaskPromptFile,
                writesDescriptionSection = step.WritesDescriptionSection,
                generationConfig = step.GenerationConfig,
                candidates = step.Candidates,
                evaluator = step.Evaluator,
                slots = step.Slots,
            },
            role = new
            {
                provider = role.Provider,
                model = role.Model,
                systemPromptFile = role.SystemPromptFile,
            },
            stateProviderParams,
        };
        return JsonSerializer.Serialize(bundle, _jsonOpts);
    }

    /// <summary>
    /// Builds the JSON serialization of a gate-check + role + state-level
    /// provider params for the gate cache key. Mirrors
    /// <see cref="SerializeStepConfig"/> but for gate-shaped steps.
    /// Provider / model / prompt file changes invalidate the gate cache.
    /// </summary>
    public static string SerializeGateConfig(
        GateCheckConfig gateCheck, WorkflowRole role,
        IReadOnlyDictionary<string, string>? stateProviderParams,
        int diffThresholdBytes)
    {
        var bundle = new
        {
            gate = new
            {
                role = gateCheck.Role,
                taskPromptFile = gateCheck.TaskPromptFile,
                taskPrompt = gateCheck.TaskPrompt,
                maxDiffChars = gateCheck.MaxDiffChars,
                maxRetries = gateCheck.MaxRetries,
                diffThresholdBytes,
            },
            role = new
            {
                provider = role.Provider,
                model = role.Model,
                systemPromptFile = role.SystemPromptFile,
            },
            stateProviderParams,
        };
        return JsonSerializer.Serialize(bundle, _jsonOpts);
    }

    /// <summary>
    /// Cache decision for the gate check. Mirrors <see cref="EvaluateAsync"/>
    /// but adapted for gate-shaped steps: there's no managed step section
    /// owned by the gate (gates don't write the description), so the section
    /// drift check is skipped — the input-hash match is the only signal.
    /// <para>
    /// Inputs to the hash bundle:
    /// </para>
    /// <list type="bullet">
    ///   <item>operator-authored description prefix</item>
    ///   <item>operator (non-aiboard-log) comments</item>
    ///   <item>upstream <c>section_output_hash</c> values from main steps that already ran</item>
    ///   <item>gate config JSON (provider/model/prompt files/diff threshold)</item>
    ///   <item>gate system prompt contents</item>
    ///   <item>fully resolved gate task prompt (placeholders substituted, including the diff and step history) — this folds the diff and persisted step records into the bundle without enumerating them separately</item>
    /// </list>
    /// </summary>
    public async Task<CacheGateResult> EvaluateGateAsync(
        string cardId,
        string cardBody,
        WorkflowState state,
        string gateConfigJson,
        string gateSystemPromptContents,
        string resolvedGatePrompt,
        IReadOnlyList<CardComment> existingComments,
        IReadOnlyList<string> upstreamSectionOutputHashes,
        CancellationToken ct)
    {
        var operatorPrefix = ExtractOperatorAuthoredPortion(cardBody);
        var operatorComments = existingComments
            .Where(c => !TaskFileManager.ContainsAgentMarker(c.Body))
            .Select(c => c.Body)
            .ToList();

        var inputs = new InputHashInputs(
            OperatorAuthoredDescription: operatorPrefix,
            OperatorComments: operatorComments,
            PriorSectionOutputHashes: upstreamSectionOutputHashes,
            StepConfigJson: gateConfigJson,
            SystemPromptContents: gateSystemPromptContents,
            TaskPromptContents: resolvedGatePrompt);

        var currentInputHash = RerunHashBuilder.ComputeInputHash(inputs);

        // Gate cache key uses the canonical step_name "gate_check"; uniqueness
        // across states comes from state_name, which is part of the lookup.
        var prior = await runStore.GetMostRecentCompleteForStepAsync(
            cardId, state.Name, "gate_check", ct);
        if (prior is null)
        {
            logger.LogDebug(
                "Gate cache miss for state '{State}' on card {Card}: no prior COMPLETE gate",
                state.Name, cardId);
            return new CacheGateResult(IsHit: false, currentInputHash, CurrentSectionOutputHash: null, Source: null);
        }

        if (!StringEquals(prior.InputHash, currentInputHash))
        {
            logger.LogInformation(
                "Gate cache miss for state '{State}' on card {Card}: input_hash differs (prior={Prior}, current={Current})",
                state.Name, cardId,
                ShortHash(prior.InputHash), ShortHash(currentInputHash));
            return new CacheGateResult(IsHit: false, currentInputHash, CurrentSectionOutputHash: null, Source: null);
        }

        logger.LogInformation(
            "Gate cache HIT for state '{State}' on card {Card}: reusing run {SourceRun} (completed {Completed})",
            state.Name, cardId, prior.RunId, prior.CompletedAtUtc);
        return new CacheGateResult(IsHit: true, currentInputHash, CurrentSectionOutputHash: null, Source: prior);
    }

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Returns everything before <c>aiboard:managed-section-start</c> as the
    /// operator-authored portion. When no managed-section markers exist
    /// (first run on a card), the whole body is operator-authored.
    /// </summary>
    private static string ExtractOperatorAuthoredPortion(string body)
    {
        if (string.IsNullOrEmpty(body)) return "";
        var idx = body.IndexOf(DescriptionWriter.ManagedStart, StringComparison.Ordinal);
        return idx < 0 ? body : body.Substring(0, idx);
    }

    private static bool StringEquals(string? a, string? b)
        => string.Equals(a, b, StringComparison.Ordinal);

    private static string ShortHash(string? h)
        => string.IsNullOrEmpty(h) ? "(null)" : (h.Length > 8 ? h[..8] : h);
}

/// <summary>
/// Outcome of a <see cref="RerunCacheGate.EvaluateAsync"/> call. The current
/// hashes are returned regardless of cache hit/miss so the caller can
/// persist them on the step_result row.
/// </summary>
public sealed record CacheGateResult(
    bool IsHit,
    string CurrentInputHash,
    string? CurrentSectionOutputHash,
    CacheCandidateRecord? Source,
    SectionHashDiagnostic? Diagnostic = null);
