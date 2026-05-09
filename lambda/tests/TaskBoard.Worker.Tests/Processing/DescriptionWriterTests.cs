using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Processing;

namespace TaskBoard.Worker.Tests.Processing;

/// <summary>
/// Unit tests for <see cref="DescriptionWriter"/> — the rerun-redesign
/// component that mechanically applies <see cref="SectionUpdate"/> directives
/// to a card description's managed-section block. Every test asserts that the
/// operator-authored prefix is preserved, that the managed-section markers
/// bracket the agent-managed area, and that the per-step section markers and
/// optional sub-blocks (open questions, resolved decisions) are emitted in
/// the documented shape.
/// </summary>
public class DescriptionWriterTests
{
    [Fact]
    public void NullUpdate_ReturnsBodyUnchanged()
    {
        // Legacy roles that don't supply section_update yet still need to
        // round-trip through ApplySectionUpdate without disturbing the body.
        // Null update → no-op.
        var body = "# Story\nDo the thing.\n";
        var result = DescriptionWriter.ApplySectionUpdate(body, "create_design", null);

        Assert.Equal(body, result);
    }

    [Fact]
    public void FirstWrite_NoManagedBlock_InitializesMarkersAndAppendsSection()
    {
        // Operator wrote requirements; agent runs first time and provides a
        // section_update. The writer should preserve the operator content,
        // append managed markers, and emit the step section inside.
        var body = "# Story\n\nRequirements: do X.";
        var update = new SectionUpdate(
            SectionUpdateStrategy.Replace,
            Content: "## Technical Design\n\nUse Postgres.");

        var result = DescriptionWriter.ApplySectionUpdate(body, "create_design", update);

        Assert.Contains("# Story", result);
        Assert.Contains("Requirements: do X.", result);
        Assert.Contains(DescriptionWriter.ManagedStart, result);
        Assert.Contains(DescriptionWriter.ManagedEnd, result);
        Assert.Contains("<!-- step-section:create_design -->", result);
        Assert.Contains("<!-- /step-section:create_design -->", result);
        Assert.Contains("## Technical Design", result);
        Assert.Contains("Use Postgres.", result);

        // Ordering: managed block must come AFTER operator content.
        var operatorIdx = result.IndexOf("Requirements: do X.", StringComparison.Ordinal);
        var startIdx = result.IndexOf(DescriptionWriter.ManagedStart, StringComparison.Ordinal);
        Assert.True(operatorIdx < startIdx, "operator content must precede managed-section start");
    }

    [Fact]
    public void Replace_BodyOnlyContent_PrependsPrettifiedStepHeading()
    {
        var body = "# Story";
        var update = new SectionUpdate(
            SectionUpdateStrategy.Replace,
            Content: "Use Postgres.");

        var result = DescriptionWriter.ApplySectionUpdate(body, "create_design", update);

        Assert.Contains("## Create Design\n\nUse Postgres.", result);
    }

    [Fact]
    public void Replace_ExistingHeading_DoesNotDuplicateHeading()
    {
        var body = "# Story";
        var update = new SectionUpdate(
            SectionUpdateStrategy.Replace,
            Content: "## Technical Design\n\nUse Postgres.");

        var result = DescriptionWriter.ApplySectionUpdate(body, "create_design", update);

        Assert.Equal(1, CountOccurrences(result, "## Technical Design"));
        Assert.DoesNotContain("## Create Design\n\n## Technical Design", result);
    }

    [Fact]
    public void Replace_OptionalBodyOnlyContent_StripsOptionalPrefixForHeading()
    {
        var body = "# Story";
        var update = new SectionUpdate(
            SectionUpdateStrategy.Replace,
            Content: "Check auth boundaries.");

        var result = DescriptionWriter.ApplySectionUpdate(body, "optional:security_review", update);

        Assert.Contains("## Security Review\n\nCheck auth boundaries.", result);
    }

    [Fact]
    public void Replace_ExistingSection_ReplacesInPlace()
    {
        // Agent is updating an already-present section. The new content must
        // replace the old verbatim (no duplication), and operator content must
        // be untouched.
        var initial = "# Story\n\nReqs.\n\n"
            + DescriptionWriter.ManagedStart + "\n"
            + "<!-- step-section:create_design -->\n"
            + "## Technical Design\n\nOLD content here.\n"
            + "<!-- /step-section:create_design -->\n\n"
            + DescriptionWriter.ManagedEnd + "\n";

        var update = new SectionUpdate(
            SectionUpdateStrategy.Replace,
            Content: "## Technical Design\n\nNEW content here.");

        var result = DescriptionWriter.ApplySectionUpdate(initial, "create_design", update);

        Assert.Contains("NEW content here.", result);
        Assert.DoesNotContain("OLD content here.", result);
        Assert.Contains("# Story", result);
        Assert.Contains("Reqs.", result);
        // Exactly one open and one close marker for this step.
        Assert.Equal(1, CountOccurrences(result, "<!-- step-section:create_design -->"));
        Assert.Equal(1, CountOccurrences(result, "<!-- /step-section:create_design -->"));
    }

    [Fact]
    public void Leave_ExistingSection_BodyUnchanged()
    {
        // strategy:leave on a section that already has content must NOT
        // disturb that content — this is the "agent confirmed nothing new"
        // path and the test guards against accidental rewrites.
        var initial = "# Story\n\nReqs.\n\n"
            + DescriptionWriter.ManagedStart + "\n"
            + "<!-- step-section:create_design -->\n"
            + "## Technical Design\n\nUnchanging content.\n"
            + "<!-- /step-section:create_design -->\n\n"
            + DescriptionWriter.ManagedEnd + "\n";

        var update = new SectionUpdate(SectionUpdateStrategy.Leave);

        var result = DescriptionWriter.ApplySectionUpdate(initial, "create_design", update);

        Assert.Contains("Unchanging content.", result);
        Assert.Equal(1, CountOccurrences(result, "<!-- step-section:create_design -->"));
    }

    [Fact]
    public void Leave_FirstRunNoSection_WritesPlaceholder()
    {
        // strategy:leave with no prior section is the "first run, agent had
        // nothing to add" case. Coerced to a placeholder so future hashing
        // (Problem 1) has a stable input.
        var body = "# Story\n\nReqs.";
        var update = new SectionUpdate(SectionUpdateStrategy.Leave);

        var result = DescriptionWriter.ApplySectionUpdate(body, "create_design", update);

        Assert.Contains(DescriptionWriter.FirstRunLeavePlaceholder, result);
        Assert.Contains("<!-- step-section:create_design -->", result);
        Assert.Contains("<!-- /step-section:create_design -->", result);
    }

    [Fact]
    public void OpenQuestions_RenderedWrappedInHtmlMarkers()
    {
        // Open Questions subsection is HTML-marker-wrapped so a parser can
        // find it robustly even after operator reformats the contents.
        // The test pins the exact marker grammar.
        var body = "# Story";
        var update = new SectionUpdate(
            SectionUpdateStrategy.Replace,
            Content: "## Technical Design\nbody.",
            OpenQuestions: ["Postgres or MySQL?", "Strong vs eventual consistency?"]);

        var result = DescriptionWriter.ApplySectionUpdate(body, "create_design", update);

        Assert.Contains("<!-- open-questions-start -->", result);
        Assert.Contains("<!-- open-questions-end -->", result);
        Assert.Contains("### Open Questions", result);
        Assert.Contains("- Postgres or MySQL?", result);
        Assert.Contains("- Strong vs eventual consistency?", result);
    }

    [Fact]
    public void ResolvedDecisions_RenderedWrappedInHtmlMarkers()
    {
        var body = "# Story";
        var update = new SectionUpdate(
            SectionUpdateStrategy.Replace,
            Content: "## Technical Design\nbody.",
            ResolvedDecisions: ["Postgres chosen over MySQL: index requirements."]);

        var result = DescriptionWriter.ApplySectionUpdate(body, "create_design", update);

        Assert.Contains("<!-- resolved-decisions-start -->", result);
        Assert.Contains("<!-- resolved-decisions-end -->", result);
        Assert.Contains("### Resolved Decisions", result);
        Assert.Contains("- Postgres chosen over MySQL: index requirements.", result);
    }

    [Fact]
    public void EmptyOpenQuestions_OmitsSubsection()
    {
        // Null/empty list → subsection is omitted entirely (no empty
        // `### Open Questions` heading with zero bullets, which would be
        // confusing for human readers).
        var body = "# Story";
        var update = new SectionUpdate(
            SectionUpdateStrategy.Replace,
            Content: "## Technical Design\nbody.",
            OpenQuestions: []);

        var result = DescriptionWriter.ApplySectionUpdate(body, "create_design", update);

        Assert.DoesNotContain("<!-- open-questions-start -->", result);
        Assert.DoesNotContain("### Open Questions", result);
    }

    [Fact]
    public void TwoStepsInSequence_AppendsInVisitationOrder()
    {
        // Step A writes its section (initialises managed block). Step B then
        // writes its section. Result must have A's section before B's, both
        // inside the managed block.
        var body = "# Story";

        var step1 = new SectionUpdate(SectionUpdateStrategy.Replace,
            Content: "## Related Tickets\n\nNone relevant.");
        var afterStep1 = DescriptionWriter.ApplySectionUpdate(body, "review_related_tickets", step1);

        var step2 = new SectionUpdate(SectionUpdateStrategy.Replace,
            Content: "## Technical Design\n\nUse Postgres.");
        var afterStep2 = DescriptionWriter.ApplySectionUpdate(afterStep1, "create_design", step2);

        var firstStepIdx = afterStep2.IndexOf("<!-- step-section:review_related_tickets -->", StringComparison.Ordinal);
        var secondStepIdx = afterStep2.IndexOf("<!-- step-section:create_design -->", StringComparison.Ordinal);

        Assert.True(firstStepIdx > 0);
        Assert.True(secondStepIdx > 0);
        Assert.True(firstStepIdx < secondStepIdx,
            "first-visited step section must appear before second-visited (visitation order)");
    }

    [Fact]
    public void OperatorSuffixAfterManagedBlock_IsPreserved()
    {
        // Operator may add notes AFTER the managed-section-end marker. Those
        // notes must round-trip through every write.
        var initial = "# Story\n\nReqs.\n\n"
            + DescriptionWriter.ManagedStart + "\n"
            + "<!-- step-section:create_design -->\n"
            + "## Technical Design\n\nOLD.\n"
            + "<!-- /step-section:create_design -->\n\n"
            + DescriptionWriter.ManagedEnd + "\n\n"
            + "## Operator's running notes\n\nThings to remember.\n";

        var update = new SectionUpdate(SectionUpdateStrategy.Replace,
            Content: "## Technical Design\n\nNEW.");

        var result = DescriptionWriter.ApplySectionUpdate(initial, "create_design", update);

        Assert.Contains("Operator's running notes", result);
        Assert.Contains("Things to remember.", result);
        Assert.Contains("NEW.", result);
        Assert.DoesNotContain("OLD.", result);

        // The operator's suffix must appear AFTER the managed-section-end marker.
        var endIdx = result.IndexOf(DescriptionWriter.ManagedEnd, StringComparison.Ordinal);
        var operatorIdx = result.IndexOf("Operator's running notes", StringComparison.Ordinal);
        Assert.True(endIdx < operatorIdx, "operator suffix must follow managed-section-end");
    }

    [Fact]
    public void CrlfBody_NormalizedToLf()
    {
        // Operators on Windows may have a card body with CRLF line endings.
        // The writer normalises to LF for predictable splitting and stable
        // hashing (Problem 1's input hash depends on this).
        var body = "# Story\r\n\r\nReqs.";
        var update = new SectionUpdate(SectionUpdateStrategy.Replace,
            Content: "## Technical Design\n\nbody.");

        var result = DescriptionWriter.ApplySectionUpdate(body, "create_design", update);

        Assert.DoesNotContain("\r\n", result);
        Assert.Contains("# Story", result);
    }

    [Fact]
    public void SecondReplaceOnSameStep_DoesNotDuplicate()
    {
        // Repeated replace calls on the same step must idempotently produce
        // exactly one section block.
        var body = "# Story";
        var first = new SectionUpdate(SectionUpdateStrategy.Replace, Content: "## Design\nv1");
        var afterFirst = DescriptionWriter.ApplySectionUpdate(body, "create_design", first);

        var second = new SectionUpdate(SectionUpdateStrategy.Replace, Content: "## Design\nv2");
        var afterSecond = DescriptionWriter.ApplySectionUpdate(afterFirst, "create_design", second);

        Assert.Equal(1, CountOccurrences(afterSecond, "<!-- step-section:create_design -->"));
        Assert.Contains("v2", afterSecond);
        Assert.DoesNotContain("v1", afterSecond);
    }

    [Fact]
    public void ManagedBlock_StartAndEndMarkers_AppearExactlyOnce()
    {
        // Every write must produce exactly one start marker and one end
        // marker — no duplication, no leakage from operator content.
        var body = "# Story";
        var step1 = new SectionUpdate(SectionUpdateStrategy.Replace, Content: "## A\nbody");
        var step2 = new SectionUpdate(SectionUpdateStrategy.Replace, Content: "## B\nbody");

        var afterStep1 = DescriptionWriter.ApplySectionUpdate(body, "step_a", step1);
        var afterStep2 = DescriptionWriter.ApplySectionUpdate(afterStep1, "step_b", step2);

        Assert.Equal(1, CountOccurrences(afterStep2, DescriptionWriter.ManagedStart));
        Assert.Equal(1, CountOccurrences(afterStep2, DescriptionWriter.ManagedEnd));
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += needle.Length;
        }
        return count;
    }
}
