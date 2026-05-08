using TaskBoard.Worker.Processing;

namespace TaskBoard.Worker.Tests.Processing;

/// <summary>
/// Unit tests for <see cref="RerunHashBuilder"/> — the rerun-redesign
/// deterministic-skip cache primitives. Every test pins a normalization rule
/// (CRLF→LF, NFC, trailing-whitespace strip, single trailing newline) so the
/// hash stays byte-stable across hosts; without that determinism, Windows
/// operators and Linux CI would produce different cache keys for the same
/// logical state.
/// </summary>
public class RerunHashBuilderTests
{
    private static InputHashInputs MinimalInputs(string description = "ops") =>
        new(description, [], [], "{}", "sys", "task");

    [Fact]
    public void ComputeInputHash_Deterministic()
    {
        // Same logical input → same hash. Sanity check before any
        // normalization-rule tests get more interesting.
        var a = RerunHashBuilder.ComputeInputHash(MinimalInputs());
        var b = RerunHashBuilder.ComputeInputHash(MinimalInputs());

        Assert.Equal(a, b);
        Assert.Equal(64, a.Length); // sha256 hex
        Assert.Matches("^[0-9a-f]{64}$", a);
    }

    [Fact]
    public void ComputeInputHash_DifferentDescriptions_DifferentHashes()
    {
        var a = RerunHashBuilder.ComputeInputHash(MinimalInputs("operator content"));
        var b = RerunHashBuilder.ComputeInputHash(MinimalInputs("different operator content"));

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ComputeInputHash_CrlfVsLf_HashIdentical()
    {
        // Operators on Windows have CRLF in card bodies; CI / Linux ones have
        // LF. The normalizer must collapse them so the cache hits across hosts.
        var crlf = MinimalInputs("hello\r\nworld\r\n");
        var lf = MinimalInputs("hello\nworld\n");

        Assert.Equal(
            RerunHashBuilder.ComputeInputHash(crlf),
            RerunHashBuilder.ComputeInputHash(lf));
    }

    [Fact]
    public void ComputeInputHash_TrailingWhitespacePerLine_HashIdentical()
    {
        // A line ending in trailing spaces is hashed the same as one without
        // them. Operators sometimes add accidental trailing space when editing.
        var spaced = MinimalInputs("line one   \nline two\n");
        var clean = MinimalInputs("line one\nline two\n");

        Assert.Equal(
            RerunHashBuilder.ComputeInputHash(spaced),
            RerunHashBuilder.ComputeInputHash(clean));
    }

    [Fact]
    public void ComputeInputHash_TrailingNewlineCount_HashIdentical()
    {
        // Content with multiple trailing newlines is normalized to a single
        // trailing newline, so the hash is stable.
        var manyNl = MinimalInputs("body\n\n\n");
        var oneNl = MinimalInputs("body\n");
        var noNl = MinimalInputs("body");

        var h1 = RerunHashBuilder.ComputeInputHash(manyNl);
        var h2 = RerunHashBuilder.ComputeInputHash(oneNl);
        var h3 = RerunHashBuilder.ComputeInputHash(noNl);

        Assert.Equal(h1, h2);
        Assert.Equal(h2, h3);
    }

    [Fact]
    public void ComputeInputHash_OperatorCommentsOrderMatters()
    {
        // Comments are concatenated in the order the caller passes them
        // (created-at order). Reordering changes the hash — operator comment
        // chronology is part of the cache key.
        var a = MinimalInputs() with { OperatorComments = ["first", "second"] };
        var b = MinimalInputs() with { OperatorComments = ["second", "first"] };

        Assert.NotEqual(
            RerunHashBuilder.ComputeInputHash(a),
            RerunHashBuilder.ComputeInputHash(b));
    }

    [Fact]
    public void ComputeInputHash_PriorSectionHashes_Included()
    {
        // Adding a prior step's section_output_hash changes the input hash
        // (transitive cache invalidation: if upstream output changed, our
        // cache key changes).
        var a = MinimalInputs() with { PriorSectionOutputHashes = ["abc123"] };
        var b = MinimalInputs() with { PriorSectionOutputHashes = ["different"] };

        Assert.NotEqual(
            RerunHashBuilder.ComputeInputHash(a),
            RerunHashBuilder.ComputeInputHash(b));
    }

    [Fact]
    public void ComputeInputHash_StepConfigChange_InvalidatesCache()
    {
        // Workflow step config (providerParams, candidate config, role
        // model) is part of the hash. Changing it invalidates the cache —
        // the agent might run with different settings.
        var a = MinimalInputs() with { StepConfigJson = "{\"effort\":\"max\"}" };
        var b = MinimalInputs() with { StepConfigJson = "{\"effort\":\"low\"}" };

        Assert.NotEqual(
            RerunHashBuilder.ComputeInputHash(a),
            RerunHashBuilder.ComputeInputHash(b));
    }

    [Fact]
    public void ComputeInputHash_SystemPromptChange_InvalidatesCache()
    {
        var a = MinimalInputs() with { SystemPromptContents = "old prompt" };
        var b = MinimalInputs() with { SystemPromptContents = "new prompt" };

        Assert.NotEqual(
            RerunHashBuilder.ComputeInputHash(a),
            RerunHashBuilder.ComputeInputHash(b));
    }

    [Fact]
    public void ComputeInputHash_TaskPromptChange_InvalidatesCache()
    {
        var a = MinimalInputs() with { TaskPromptContents = "old" };
        var b = MinimalInputs() with { TaskPromptContents = "new" };

        Assert.NotEqual(
            RerunHashBuilder.ComputeInputHash(a),
            RerunHashBuilder.ComputeInputHash(b));
    }

    [Fact]
    public void ComputeSectionHash_HappyPath()
    {
        var body =
            "# Story\n" +
            "<!-- aiboard:managed-section-start -->\n" +
            "<!-- step-section:create_design -->\n" +
            "## Technical Design\nUse Postgres.\n" +
            "<!-- /step-section:create_design -->\n" +
            "<!-- aiboard:managed-section-end -->\n";

        var hash = RerunHashBuilder.ComputeSectionHash(body, "create_design");

        Assert.NotNull(hash);
        Assert.Equal(64, hash!.Length);
    }

    [Fact]
    public void ComputeSectionHash_MissingSection_ReturnsNull()
    {
        // No section for the given step name → null. Caller treats this as
        // "section drift / missing — don't cache."
        var body = "# Story\nNo managed section here.\n";

        var hash = RerunHashBuilder.ComputeSectionHash(body, "create_design");

        Assert.Null(hash);
    }

    [Fact]
    public void ComputeSectionHash_MalformedNoCloseMarker_ReturnsNull()
    {
        // Open marker without a close marker — the writer's diagnostic path
        // will fire; the hash returns null so the cache misses.
        var body =
            "# Story\n" +
            "<!-- aiboard:managed-section-start -->\n" +
            "<!-- step-section:create_design -->\n" +
            "## Design\n";  // no close marker

        var hash = RerunHashBuilder.ComputeSectionHash(body, "create_design");

        Assert.Null(hash);
    }

    [Fact]
    public void ComputeSectionHash_DifferentContent_DifferentHashes()
    {
        var body1 =
            "<!-- step-section:create_design -->\n" +
            "v1 content\n" +
            "<!-- /step-section:create_design -->";
        var body2 =
            "<!-- step-section:create_design -->\n" +
            "v2 content\n" +
            "<!-- /step-section:create_design -->";

        var h1 = RerunHashBuilder.ComputeSectionHash(body1, "create_design");
        var h2 = RerunHashBuilder.ComputeSectionHash(body2, "create_design");

        Assert.NotEqual(h1, h2);
    }

    [Fact]
    public void ComputeSectionHash_TrailingWhitespaceWithinSection_StableHash()
    {
        // Same content with trailing spaces on lines → same hash. Makes the
        // cache robust to trivial operator edits.
        var body1 =
            "<!-- step-section:create_design -->\n" +
            "line one   \nline two\n" +  // trailing spaces
            "<!-- /step-section:create_design -->";
        var body2 =
            "<!-- step-section:create_design -->\n" +
            "line one\nline two\n" +     // clean
            "<!-- /step-section:create_design -->";

        Assert.Equal(
            RerunHashBuilder.ComputeSectionHash(body1, "create_design"),
            RerunHashBuilder.ComputeSectionHash(body2, "create_design"));
    }

    [Fact]
    public void Canonicalize_NfcNormalization_Stable()
    {
        // NFC normalization collapses combining-character forms. "café"
        // composed differently must hash the same.
        var composed = "café";       // single 'é' codepoint
        var decomposed = "café";     // 'e' + combining acute

        Assert.Equal(
            RerunHashBuilder.Canonicalize(composed),
            RerunHashBuilder.Canonicalize(decomposed));
    }

    [Fact]
    public void Canonicalize_EmptyOrNull_ReturnsNewlineOnly()
    {
        Assert.Equal("\n", RerunHashBuilder.Canonicalize(""));
        Assert.Equal("\n", RerunHashBuilder.Canonicalize(null));
    }

    [Fact]
    public void Canonicalize_Idempotent()
    {
        // Applying canonicalize twice is the same as once — guards against a
        // future change that breaks idempotency (e.g. accidentally double-
        // adding trailing newlines on each pass).
        var input = "line one  \r\nline two\r\n\r\n";
        var once = RerunHashBuilder.Canonicalize(input);
        var twice = RerunHashBuilder.Canonicalize(once);

        Assert.Equal(once, twice);
    }

    // ── Finding 8: malformed / duplicate section marker detection ───────
    //
    // Pre-fix, ComputeSectionHash silently took the first IndexOf occurrence
    // when a step section's open/close marker appeared more than once,
    // producing a hash for whichever pair appeared first. That hides a real
    // structural failure — multiple managed sections for the same step name
    // mean DescriptionWriter.ApplySectionUpdate would only rewrite one of
    // them, leaving the others stale. The fix returns null + a structured
    // SectionHashDiagnostic so the caller can log and the cache misses,
    // forcing the step to re-run and (presumably) clean up the duplicate.

    [Fact]
    public void ComputeSectionHash_DuplicateOpenMarker_ReturnsNullWithDiagnostic()
    {
        // Two `<!-- step-section:create_design -->` markers — operator likely
        // copy/pasted a section, or a buggy writer ran twice. The hash builder
        // refuses to pick one and returns null with a Duplicate diagnostic.
        var body =
            "<!-- step-section:create_design -->\n" +
            "first content\n" +
            "<!-- /step-section:create_design -->\n" +
            "<!-- step-section:create_design -->\n" +
            "second content\n" +
            "<!-- /step-section:create_design -->\n";

        var hash = RerunHashBuilder.ComputeSectionHash(
            body, "create_design", out var diagnostic);

        Assert.Null(hash);
        Assert.NotNull(diagnostic);
        Assert.Equal("create_design", diagnostic!.StepName);
        Assert.Equal(SectionHashDiagnosticKind.DuplicateOpenMarker, diagnostic.Kind);
        Assert.Equal(2, diagnostic.Count);
    }

    [Fact]
    public void ComputeSectionHash_DuplicateCloseMarker_ReturnsNullWithDiagnostic()
    {
        // One open marker, two close markers — also structurally broken.
        // The hash builder returns null so the cache misses; left to its
        // own devices, IndexOf would have settled on the first close, but
        // the operator can't reason about which content is "current" when
        // there are stray close markers.
        var body =
            "<!-- step-section:create_design -->\n" +
            "content one\n" +
            "<!-- /step-section:create_design -->\n" +
            "stray text\n" +
            "<!-- /step-section:create_design -->\n";

        var hash = RerunHashBuilder.ComputeSectionHash(
            body, "create_design", out var diagnostic);

        Assert.Null(hash);
        Assert.NotNull(diagnostic);
        Assert.Equal(SectionHashDiagnosticKind.DuplicateCloseMarker, diagnostic.Kind);
        Assert.Equal(2, diagnostic.Count);
    }

    [Fact]
    public void ComputeSectionHash_MissingCloseMarker_ReturnsDiagnostic()
    {
        // Open marker without a close marker — the writer was interrupted
        // or the body was hand-edited. Same null-with-diagnostic shape so
        // call sites can surface the issue.
        var body =
            "<!-- step-section:create_design -->\n" +
            "## Design\n";

        var hash = RerunHashBuilder.ComputeSectionHash(
            body, "create_design", out var diagnostic);

        Assert.Null(hash);
        Assert.NotNull(diagnostic);
        Assert.Equal(SectionHashDiagnosticKind.MissingCloseMarker, diagnostic.Kind);
    }

    [Fact]
    public void ComputeSectionHash_MissingOpenMarker_ReturnsNullWithoutDiagnostic()
    {
        // Section simply isn't there yet (first run before any agent wrote
        // it). Not a structural failure — the cache treats it as "no prior
        // section" and the step runs full. No diagnostic emitted because
        // there's nothing to surface to the operator.
        var body = "# Story\nNo managed section yet.\n";

        var hash = RerunHashBuilder.ComputeSectionHash(
            body, "create_design", out var diagnostic);

        Assert.Null(hash);
        Assert.Null(diagnostic);
    }

    [Fact]
    public void ComputeSectionHash_HappyPath_NoDiagnostic()
    {
        // Well-formed single section produces a hash and no diagnostic —
        // the diagnostic out-parameter is reserved for malformed input.
        var body =
            "<!-- step-section:create_design -->\n" +
            "## Technical Design\nUse Postgres.\n" +
            "<!-- /step-section:create_design -->\n";

        var hash = RerunHashBuilder.ComputeSectionHash(
            body, "create_design", out var diagnostic);

        Assert.NotNull(hash);
        Assert.Equal(64, hash!.Length);
        Assert.Null(diagnostic);
    }

    [Fact]
    public void ComputeSectionHash_DuplicateOpenForOtherStep_DoesNotAffectThisStep()
    {
        // Duplicate markers for step 'foo' should not poison the hash for
        // step 'bar'. The diagnostic / null return is per-step-name —
        // ComputeSectionHash counts only markers carrying its argument's
        // step name. Without this isolation, one step's malformed body
        // would force every step on the card to re-run.
        var body =
            "<!-- step-section:foo -->\nfoo1\n<!-- /step-section:foo -->\n" +
            "<!-- step-section:foo -->\nfoo2\n<!-- /step-section:foo -->\n" +
            "<!-- step-section:bar -->\nbar content\n<!-- /step-section:bar -->\n";

        var fooHash = RerunHashBuilder.ComputeSectionHash(body, "foo", out var fooDiag);
        var barHash = RerunHashBuilder.ComputeSectionHash(body, "bar", out var barDiag);

        Assert.Null(fooHash);
        Assert.NotNull(fooDiag);
        Assert.NotNull(barHash);
        Assert.Null(barDiag);
    }

    // ── Finding 8: backward-compat overload returns hash without diagnostic ──
    //
    // The single-argument overload (kept for callers that don't care about
    // the diagnostic) must match the two-argument overload's null/non-null
    // decision exactly. Pinned so future refactors can't subtly diverge the
    // two paths.

    [Fact]
    public void ComputeSectionHash_OverloadParity_OnDuplicate()
    {
        var body =
            "<!-- step-section:s -->\na\n<!-- /step-section:s -->\n" +
            "<!-- step-section:s -->\nb\n<!-- /step-section:s -->\n";

        var simple = RerunHashBuilder.ComputeSectionHash(body, "s");
        var withDiag = RerunHashBuilder.ComputeSectionHash(body, "s", out var diag);

        Assert.Null(simple);
        Assert.Null(withDiag);
        Assert.NotNull(diag);
    }

    [Fact]
    public void ComputeSectionHash_OverloadParity_OnHappyPath()
    {
        var body = "<!-- step-section:s -->\nx\n<!-- /step-section:s -->\n";

        var simple = RerunHashBuilder.ComputeSectionHash(body, "s");
        var withDiag = RerunHashBuilder.ComputeSectionHash(body, "s", out var diag);

        Assert.Equal(simple, withDiag);
        Assert.Null(diag);
    }
}
