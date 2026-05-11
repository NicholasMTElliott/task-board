using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Models;

namespace TaskBoard.Worker.Processing;

/// <summary>
/// Role-level fallback walker for single-agent invocation sites (gates,
/// evaluators, optional reviewers, simple steps).
///
/// <para>
/// Schema-driven: each <see cref="WorkflowRole"/> may declare a
/// <see cref="WorkflowRole.Fallbacks"/> chain. When the primary provider throws
/// an executor-side exception classified to a <see cref="FailureReason"/>
/// listed in the role's <see cref="WorkflowRole.FallbackOn"/> (default: every
/// non-cancellation executor failure category), the runtime walks the chain
/// until a fallback succeeds or all attempts are exhausted.
/// </para>
///
/// <para>
/// Trigger taxonomy: a <see cref="RateLimitException"/>,
/// <see cref="CliInfrastructureException"/>, or <see cref="TimeoutException"/>
/// classifies to the corresponding <see cref="FailureReason"/>; everything
/// else classifies to <see cref="FailureReason.AGENT_ERROR"/>. The agent's own
/// in-band <c>outcome=ERROR</c> verdict is NOT a fallback trigger because it
/// returns an <see cref="AgentResult"/> instead of throwing — that's a quality
/// signal handled by workflow transitions, not provider redundancy.
/// </para>
///
/// <para>
/// Cancellation (<see cref="OperationCanceledException"/>) is NEVER caught
/// here — Ctrl+C and shutdown propagate immediately regardless of FallbackOn.
/// </para>
/// </summary>
internal static class RoleInvoker
{
    private static readonly IReadOnlySet<FailureReason> DefaultFallbackOn =
        new HashSet<FailureReason>
        {
            FailureReason.RATE_LIMIT,
            FailureReason.AGENT_ERROR,
            FailureReason.INFRASTRUCTURE,
            FailureReason.TIMEOUT,
        };

    /// <summary>
    /// One attempt in the fallback chain. Index 0 is the primary; index 1+ are
    /// fallbacks taken from <see cref="WorkflowRole.Fallbacks"/>.
    /// </summary>
    internal sealed record Attempt(
        int Index,
        string Provider,
        string Model,
        IReadOnlyDictionary<string, string>? ProviderParams);

    /// <summary>
    /// Builds the ordered attempt list: primary first, then each fallback. A
    /// fallback's <see cref="RoleFallback.Model"/> defaults to the role's
    /// model; its <see cref="RoleFallback.ProviderParams"/> overlay the
    /// caller-supplied <paramref name="primaryProviderParams"/>.
    /// </summary>
    internal static IReadOnlyList<Attempt> BuildAttempts(
        WorkflowRole role,
        IReadOnlyDictionary<string, string>? primaryProviderParams)
    {
        var attempts = new List<Attempt>(1 + (role.Fallbacks?.Count ?? 0))
        {
            new(0, role.Provider, role.Model, primaryProviderParams),
        };

        if (role.Fallbacks is { Count: > 0 } fallbacks)
        {
            for (var i = 0; i < fallbacks.Count; i++)
            {
                var fb = fallbacks[i];
                var model = string.IsNullOrWhiteSpace(fb.Model) ? role.Model : fb.Model;
                var mergedParams = MergeParams(primaryProviderParams, fb.ProviderParams);
                attempts.Add(new(i + 1, fb.Provider, model, mergedParams));
            }
        }

        return attempts;
    }

    /// <summary>
    /// Returns the role's effective fallback-on set, or the default
    /// every non-cancellation executor failure category when the role didn't
    /// declare one.
    /// </summary>
    internal static IReadOnlySet<FailureReason> EffectiveFallbackOn(WorkflowRole role)
    {
        if (role.FallbackOn is null or { Count: 0 })
            return DefaultFallbackOn;
        return new HashSet<FailureReason>(role.FallbackOn);
    }

    /// <summary>
    /// True iff the exception classifies to a <see cref="FailureReason"/> in
    /// <paramref name="fallbackOn"/>. Cancellation is never a fallback trigger.
    /// </summary>
    internal static bool ShouldFallback(Exception ex, IReadOnlySet<FailureReason> fallbackOn)
    {
        if (ex is OperationCanceledException) return false;
        return fallbackOn.Contains(Classify(ex));
    }

    /// <summary>
    /// Maps an executor-thrown exception to a <see cref="FailureReason"/> for
    /// logging / fallback decisions. Mirrors
    /// <c>AgentRunner.ClassifyFailure</c>.
    /// </summary>
    internal static FailureReason Classify(Exception ex) => ex switch
    {
        RateLimitException => FailureReason.RATE_LIMIT,
        TimeoutException => FailureReason.TIMEOUT,
        CliInfrastructureException => FailureReason.INFRASTRUCTURE,
        _ => FailureReason.AGENT_ERROR,
    };

    /// <summary>
    /// Builds the per-attempt <see cref="AgentExecutionContext"/>. Attempt 0
    /// uses the caller's primary context as-is; attempts 1+ swap in the
    /// fallback's model and (merged) provider params.
    /// </summary>
    internal static AgentExecutionContext BuildAttemptContext(
        AgentExecutionContext primaryContext,
        Attempt attempt)
    {
        if (attempt.Index == 0)
            return primaryContext;

        return primaryContext with
        {
            Model = attempt.Model,
            ProviderParams = attempt.ProviderParams,
        };
    }

    private static IReadOnlyDictionary<string, string>? MergeParams(
        IReadOnlyDictionary<string, string>? primary,
        Dictionary<string, string>? fallbackOverlay)
    {
        if (fallbackOverlay is null or { Count: 0 }) return primary;
        if (primary is null or { Count: 0 }) return fallbackOverlay;

        var merged = new Dictionary<string, string>(primary, StringComparer.Ordinal);
        foreach (var (k, v) in fallbackOverlay)
            merged[k] = v;
        return merged;
    }
}
