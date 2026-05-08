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
}
