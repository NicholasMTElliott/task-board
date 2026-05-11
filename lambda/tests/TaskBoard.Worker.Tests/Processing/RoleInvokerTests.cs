using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Models;
using TaskBoard.Worker.Processing;

namespace TaskBoard.Worker.Tests.Processing;

/// <summary>
/// Unit tests for the role-fallback walker. Pinned behaviour:
/// <list type="bullet">
///   <item>Attempt 0 = primary; attempts 1..N = each <see cref="RoleFallback"/>.</item>
///   <item>Fallback Model defaults to role.Model when its own Model is null.</item>
///   <item>Fallback ProviderParams overlay the caller-supplied primary params.</item>
///   <item>Default <c>FallbackOn</c> = every non-cancellation executor failure
///         category.</item>
///   <item>Cancellation is never a fallback trigger.</item>
///   <item>AGENT_ERROR as an exception category can trigger fallback; an
///         in-band ERROR verdict is an AgentResult, not an exception.</item>
/// </list>
/// </summary>
public class RoleInvokerTests
{
    private static WorkflowRole MakeRole(
        string provider = "docker-claude-cli",
        string model = "claude-opus-4-6",
        List<RoleFallback>? fallbacks = null,
        List<FailureReason>? fallbackOn = null) =>
        new(model, "system prompt", new List<string>(), null, provider, fallbacks, fallbackOn);

    // ── BuildAttempts ─────────────────────────────────────────────────────

    [Fact]
    public void BuildAttempts_NoFallbacks_ReturnsSingletonPrimary()
    {
        var role = MakeRole("docker-claude-cli", "claude-opus-4-6");

        var attempts = RoleInvoker.BuildAttempts(role, primaryProviderParams: null);

        Assert.Single(attempts);
        Assert.Equal(0, attempts[0].Index);
        Assert.Equal("docker-claude-cli", attempts[0].Provider);
        Assert.Equal("claude-opus-4-6", attempts[0].Model);
    }

    [Fact]
    public void BuildAttempts_TwoFallbacks_ReturnsThreeAttemptsInOrder()
    {
        var role = MakeRole(
            "docker-claude-cli", "claude-opus-4-6",
            fallbacks: new List<RoleFallback>
            {
                new("docker-codex", Model: "gpt-5.5"),
                new("docker-opencode", Model: "qwen3.6-35b-a3b"),
            });

        var attempts = RoleInvoker.BuildAttempts(role, primaryProviderParams: null);

        Assert.Equal(3, attempts.Count);
        Assert.Equal((0, "docker-claude-cli", "claude-opus-4-6"),
            (attempts[0].Index, attempts[0].Provider, attempts[0].Model));
        Assert.Equal((1, "docker-codex", "gpt-5.5"),
            (attempts[1].Index, attempts[1].Provider, attempts[1].Model));
        Assert.Equal((2, "docker-opencode", "qwen3.6-35b-a3b"),
            (attempts[2].Index, attempts[2].Provider, attempts[2].Model));
    }

    [Fact]
    public void BuildAttempts_FallbackWithNullModel_InheritsRoleModel()
    {
        // Important corner: a same-provider fallback with no Model pin should
        // get the role's primary model. Cross-provider fallbacks SHOULD be
        // pinning their own model (validator audits that), but if they don't,
        // the role model is the only sensible default we have.
        var role = MakeRole("docker-claude-cli", "claude-opus-4-6",
            fallbacks: new List<RoleFallback> { new("docker-claude-cli") }); // Model=null

        var attempts = RoleInvoker.BuildAttempts(role, null);

        Assert.Equal(2, attempts.Count);
        Assert.Equal("claude-opus-4-6", attempts[1].Model);
    }

    [Fact]
    public void BuildAttempts_FallbackProviderParams_OverlayPrimary()
    {
        var primary = new Dictionary<string, string>
        {
            ["effort"] = "max",
            ["sandbox"] = "read-only",
        };
        var role = MakeRole("claude-cli", "opus",
            fallbacks: new List<RoleFallback>
            {
                new("docker-codex", Model: "gpt-5.4-mini",
                    ProviderParams: new Dictionary<string, string>
                    {
                        ["effort"] = "low",       // override
                        ["yolo"]   = "true",      // new
                    }),
            });

        var attempts = RoleInvoker.BuildAttempts(role, primary);

        var fallback = attempts[1];
        Assert.NotNull(fallback.ProviderParams);
        Assert.Equal("low", fallback.ProviderParams!["effort"]);
        Assert.Equal("read-only", fallback.ProviderParams["sandbox"]);   // inherited
        Assert.Equal("true", fallback.ProviderParams["yolo"]);           // added
        // Primary's params unchanged (records are immutable; merge produced new dict)
        Assert.Equal("max", primary["effort"]);
    }

    [Fact]
    public void BuildAttempts_PrimaryHasParams_FallbackHasNone_FallbackInheritsPrimary()
    {
        var primary = new Dictionary<string, string> { ["effort"] = "max" };
        var role = MakeRole("a", "m", fallbacks: new List<RoleFallback> { new("b", "m2") });

        var attempts = RoleInvoker.BuildAttempts(role, primary);

        Assert.Equal("max", attempts[1].ProviderParams!["effort"]);
    }

    // ── EffectiveFallbackOn ───────────────────────────────────────────────

    [Fact]
    public void EffectiveFallbackOn_Null_UsesEveryNonCancellationFailureCategory()
    {
        var role = MakeRole(fallbackOn: null);

        var set = RoleInvoker.EffectiveFallbackOn(role);

        Assert.Equal(4, set.Count);
        Assert.Contains(FailureReason.RATE_LIMIT, set);
        Assert.Contains(FailureReason.AGENT_ERROR, set);
        Assert.Contains(FailureReason.INFRASTRUCTURE, set);
        Assert.Contains(FailureReason.TIMEOUT, set);
    }

    [Fact]
    public void EffectiveFallbackOn_Empty_UsesDefault()
    {
        var role = MakeRole(fallbackOn: new List<FailureReason>());

        var set = RoleInvoker.EffectiveFallbackOn(role);

        Assert.Equal(4, set.Count);
    }

    [Fact]
    public void EffectiveFallbackOn_Custom_HonoursOperatorChoice()
    {
        var role = MakeRole(fallbackOn: new List<FailureReason>
        {
            FailureReason.RATE_LIMIT,
            FailureReason.TIMEOUT,
        });

        var set = RoleInvoker.EffectiveFallbackOn(role);

        Assert.Equal(2, set.Count);
        Assert.Contains(FailureReason.RATE_LIMIT, set);
        Assert.Contains(FailureReason.TIMEOUT, set);
        Assert.DoesNotContain(FailureReason.INFRASTRUCTURE, set);
    }

    // ── ShouldFallback ────────────────────────────────────────────────────

    [Fact]
    public void ShouldFallback_RateLimit_InDefaultSet_True()
    {
        var ex = new RateLimitException("limit hit", RateLimitSource.AgentCli);

        var result = RoleInvoker.ShouldFallback(ex,
            new HashSet<FailureReason> { FailureReason.RATE_LIMIT, FailureReason.INFRASTRUCTURE });

        Assert.True(result);
    }

    [Fact]
    public void ShouldFallback_Infrastructure_InDefaultSet_True()
    {
        var ex = new CliInfrastructureException("exit 127: claude: command not found");

        var result = RoleInvoker.ShouldFallback(ex,
            new HashSet<FailureReason> { FailureReason.RATE_LIMIT, FailureReason.INFRASTRUCTURE });

        Assert.True(result);
    }

    [Fact]
    public void ShouldFallback_Timeout_InDefaultSet_True()
    {
        var ex = new TimeoutException();

        var result = RoleInvoker.ShouldFallback(ex,
            RoleInvoker.EffectiveFallbackOn(MakeRole()));

        Assert.True(result);
    }

    [Fact]
    public void ShouldFallback_Timeout_OptedIn_True()
    {
        var ex = new TimeoutException();

        var result = RoleInvoker.ShouldFallback(ex,
            new HashSet<FailureReason> { FailureReason.TIMEOUT });

        Assert.True(result);
    }

    [Fact]
    public void ShouldFallback_Cancellation_NeverTriggers()
    {
        var ex = new OperationCanceledException();

        // Even when the set explicitly includes AGENT_ERROR (which would
        // match the classifier's catchall), cancellation must never trigger
        // fallback — Ctrl+C / shutdown propagates immediately.
        var result = RoleInvoker.ShouldFallback(ex,
            new HashSet<FailureReason>
            {
                FailureReason.RATE_LIMIT,
                FailureReason.INFRASTRUCTURE,
                FailureReason.TIMEOUT,
                FailureReason.AGENT_ERROR,
            });

        Assert.False(result);
    }

    [Fact]
    public void ShouldFallback_GenericException_ClassifiesAgentError_InDefaultSet()
    {
        // Agent.ExecuteAsync threw something unexpected (parser bug, JSON
        // failure). Classified as AGENT_ERROR. Default fallback set includes it
        // because no valid AgentResult exists yet.
        var ex = new InvalidOperationException("parser failed");

        var result = RoleInvoker.ShouldFallback(ex,
            RoleInvoker.EffectiveFallbackOn(MakeRole()));

        Assert.True(result);
    }

    [Fact]
    public void ShouldFallback_GenericException_CustomSetCanExcludeAgentError()
    {
        var ex = new InvalidOperationException("parser failed");

        var result = RoleInvoker.ShouldFallback(ex,
            new HashSet<FailureReason> { FailureReason.RATE_LIMIT, FailureReason.INFRASTRUCTURE });

        Assert.False(result);
    }

    // ── Classify ──────────────────────────────────────────────────────────

    [Fact]
    public void Classify_MapsExceptionTypesCorrectly()
    {
        Assert.Equal(FailureReason.RATE_LIMIT,
            RoleInvoker.Classify(new RateLimitException("x", RateLimitSource.AgentCli)));
        Assert.Equal(FailureReason.INFRASTRUCTURE,
            RoleInvoker.Classify(new CliInfrastructureException("x")));
        Assert.Equal(FailureReason.TIMEOUT,
            RoleInvoker.Classify(new TimeoutException()));
        Assert.Equal(FailureReason.AGENT_ERROR,
            RoleInvoker.Classify(new InvalidOperationException()));
        Assert.Equal(FailureReason.AGENT_ERROR,
            RoleInvoker.Classify(new Exception("generic")));
    }

    // ── BuildAttemptContext ───────────────────────────────────────────────

    [Fact]
    public void BuildAttemptContext_Index0_ReturnsPrimaryUnchanged()
    {
        var primary = new AgentExecutionContext(
            "card-1", "Title", "/ws", "Task prompt", "/sys.md",
            Model: "primary-model",
            ProviderParams: new Dictionary<string, string> { ["k"] = "v" });
        var attempt = new RoleInvoker.Attempt(0, "primary-prov", "primary-model",
            primary.ProviderParams);

        var built = RoleInvoker.BuildAttemptContext(primary, attempt);

        Assert.Same(primary, built);
    }

    [Fact]
    public void BuildAttemptContext_Index1_SwapsModelAndParams()
    {
        var primary = new AgentExecutionContext(
            "card-1", "Title", "/ws", "Task prompt", "/sys.md",
            Model: "primary-model",
            ProviderParams: new Dictionary<string, string> { ["a"] = "1" });
        var attempt = new RoleInvoker.Attempt(1, "fallback-prov", "fallback-model",
            new Dictionary<string, string> { ["a"] = "1", ["b"] = "2" });

        var built = RoleInvoker.BuildAttemptContext(primary, attempt);

        Assert.NotSame(primary, built);
        Assert.Equal("fallback-model", built.Model);
        Assert.Equal("/ws", built.WorkspacePath);
        Assert.Equal("Task prompt", built.TaskPrompt);
        Assert.Equal(2, built.ProviderParams!.Count);
        Assert.Equal("2", built.ProviderParams["b"]);
    }
}
