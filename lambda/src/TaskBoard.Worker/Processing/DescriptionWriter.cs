using System.Text;
using System.Text.RegularExpressions;
using TaskBoard.Worker.Clients;

namespace TaskBoard.Worker.Processing;

/// <summary>
/// Mechanically applies a <see cref="SectionUpdate"/> to a card description's
/// managed-section block (rerun redesign, Problem 2). Pure function — no I/O,
/// no state — so the policy is testable in isolation and the AgentRunner just
/// calls <see cref="ApplySectionUpdate"/> per completed step.
///
/// <para>Description shape:</para>
/// <code>
/// &lt;operator-authored content&gt;
///
/// &lt;!-- aiboard:managed-section-start --&gt;
/// &lt;!-- step-section:NAME --&gt;
/// (current truth — concise, polished, latest iteration)
/// &lt;!-- /step-section:NAME --&gt;
/// (additional step sections in visitation order)
/// &lt;!-- aiboard:managed-section-end --&gt;
///
/// &lt;operator-authored suffix, if any&gt;
/// </code>
///
/// <para>
/// On first write to a card, the writer initialises the managed-section
/// markers at the end of the existing description (preserving everything
/// before as operator-authored). Subsequent writes for that step replace
/// the existing section in place; new step sections from later visitations
/// are appended at the end of the managed block (visitation order).
/// </para>
/// </summary>
internal static partial class DescriptionWriter
{
    internal const string ManagedStart = "<!-- aiboard:managed-section-start -->";
    internal const string ManagedEnd = "<!-- aiboard:managed-section-end -->";

    private const string OpenQuestionsStart = "<!-- open-questions-start -->";
    private const string OpenQuestionsEnd = "<!-- open-questions-end -->";
    private const string ResolvedDecisionsStart = "<!-- resolved-decisions-start -->";
    private const string ResolvedDecisionsEnd = "<!-- resolved-decisions-end -->";

    /// <summary>
    /// First-run placeholder for steps that returned <see cref="SectionUpdateStrategy.Leave"/>
    /// when no prior section existed. Coerced (rather than skipping the
    /// section) so future hashing has a stable input — the Problem 1
    /// deterministic-skip path keys on the section's normalized hash.
    /// </summary>
    internal const string FirstRunLeavePlaceholder = "_No durable section update from this step._";

    /// <summary>
    /// Returns a new card body with <paramref name="update"/> applied to the
    /// step section keyed by <paramref name="stepName"/>.
    /// <para>
    /// When <paramref name="update"/> is null, returns the body unchanged
    /// (equivalent to "agent declined to provide a section_update field" — for
    /// legacy roles this is the path used until system prompts are updated).
    /// </para>
    /// </summary>
    /// <param name="body">The current card body.</param>
    /// <param name="stepName">The step name keying the section (raw name, not the kind:slot suffix variants).</param>
    /// <param name="update">Agent's directive. Null → no-op.</param>
    /// <returns>The new card body.</returns>
    internal static string ApplySectionUpdate(string body, string stepName, SectionUpdate? update)
    {
        if (update is null)
            return body;

        // Normalize body line endings to LF for predictable splitting. Most
        // boards (GitHub, Trello) round-trip LF; emitting LF keeps managed
        // sections byte-stable across hosts (Problem 1's hash relies on this).
        body ??= "";
        body = body.Replace("\r\n", "\n");

        var split = SplitManagedBlock(body);

        // Find existing step section in the managed block (if any).
        var (sectionExists, existingStart, existingEnd) = FindStepSection(split.ManagedBlock, stepName);

        // Compute new section content per strategy.
        string? newSection;
        switch (update.Strategy)
        {
            case SectionUpdateStrategy.Leave:
                if (sectionExists)
                {
                    // Existing section preserved; managedBlock unchanged.
                    return Recompose(split);
                }
                // First-run placeholder: keep a stable section so downstream
                // hashing has something to compare against.
                newSection = BuildSection(stepName, FirstRunLeavePlaceholder, openQuestions: null, resolvedDecisions: null);
                break;

            case SectionUpdateStrategy.Replace:
            case SectionUpdateStrategy.AppendWithRevisionNotes:
                // Both render the agent-supplied content as the new section.
                // append_with_revision_notes differs only in that the agent
                // includes lessons-learned text inside `content` itself; the
                // orchestrator is mechanical either way. Content MUST be
                // present here — the parser already rejects null/empty
                // content for non-leave strategies, but defensive bail-out
                // keeps the writer from emitting a bogus placeholder section
                // if it ever sees one (rerun redesign Finding 8).
                if (string.IsNullOrWhiteSpace(update.Content))
                    return body;
                newSection = BuildSection(stepName, update.Content, update.OpenQuestions, update.ResolvedDecisions);
                break;

            default:
                return body; // Unknown strategy — defensive no-op.
        }

        // Apply: replace in place if the section exists; append at end of
        // managed block (visitation order) otherwise.
        string newManagedBlock;
        if (sectionExists)
        {
            newManagedBlock = split.ManagedBlock.Substring(0, existingStart)
                + newSection
                + split.ManagedBlock.Substring(existingEnd);
        }
        else
        {
            // Append to end of managed block. Trim trailing whitespace so
            // sections stack cleanly, then add a single blank line separator.
            var trimmed = split.ManagedBlock.TrimEnd('\n', ' ', '\t');
            var separator = trimmed.Length == 0 ? "" : "\n\n";
            newManagedBlock = trimmed + separator + newSection;
        }

        return Recompose(split with { ManagedBlock = newManagedBlock });
    }

    // ── Helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Splits the body into prefix / managedBlock / suffix, with a flag
    /// indicating whether managed-section markers were present.
    /// </summary>
    private record struct SplitResult(string Prefix, string ManagedBlock, string Suffix, bool HadMarkers);

    private static SplitResult SplitManagedBlock(string body)
    {
        var startIdx = body.IndexOf(ManagedStart, StringComparison.Ordinal);
        if (startIdx < 0)
        {
            // No managed block yet — first write will initialise it at end of body.
            return new SplitResult(body, "", "", HadMarkers: false);
        }

        var blockStart = startIdx + ManagedStart.Length;
        var endIdx = body.IndexOf(ManagedEnd, blockStart, StringComparison.Ordinal);
        if (endIdx < 0)
        {
            // Open-but-not-closed: treat everything from the start marker to
            // end-of-body as the block. This is forgiving against operator
            // edits that accidentally drop the closing marker.
            return new SplitResult(body.Substring(0, startIdx), body.Substring(blockStart), "", HadMarkers: true);
        }

        var prefix = body.Substring(0, startIdx);
        var managedBlock = body.Substring(blockStart, endIdx - blockStart);
        var suffix = body.Substring(endIdx + ManagedEnd.Length);
        return new SplitResult(prefix, managedBlock, suffix, HadMarkers: true);
    }

    /// <summary>
    /// Locates the existing step section within the managed block.
    /// Returns (exists, contentStartIndex, contentEndIndex) where the
    /// indices bracket the entire `&lt;!-- step-section:NAME --&gt;...`
    /// `&lt;!-- /step-section:NAME --&gt;` span (so a slice replacement is a
    /// straightforward Substring concat). Indices are relative to managedBlock.
    /// </summary>
    private static (bool Exists, int Start, int End) FindStepSection(string managedBlock, string stepName)
    {
        var openMarker = $"<!-- step-section:{stepName} -->";
        var closeMarker = $"<!-- /step-section:{stepName} -->";

        var openIdx = managedBlock.IndexOf(openMarker, StringComparison.Ordinal);
        if (openIdx < 0)
            return (false, -1, -1);

        var closeIdx = managedBlock.IndexOf(closeMarker, openIdx + openMarker.Length, StringComparison.Ordinal);
        if (closeIdx < 0)
        {
            // Malformed (open without close). Treat as not-exists; a fresh
            // section will be appended. The diagnostics for "malformed
            // markers" live in the AgentRunner cache-decision path (Problem 1)
            // — the writer is forgiving here so a single typo doesn't block
            // every subsequent run.
            return (false, -1, -1);
        }

        // Include trailing newlines so the replacement preserves separators.
        var endOfClose = closeIdx + closeMarker.Length;

        return (true, openIdx, endOfClose);
    }

    /// <summary>
    /// Builds the full step-section block (open marker, content, optional
    /// open-questions / resolved-decisions wrapped subsections, close marker).
    /// Content is trimmed of trailing whitespace before insertion; subsections
    /// are omitted when the supplied list is null or empty.
    /// </summary>
    private static string BuildSection(
        string stepName,
        string content,
        IReadOnlyList<string>? openQuestions,
        IReadOnlyList<string>? resolvedDecisions)
    {
        // All separators emit '\n' explicitly (not StringBuilder.AppendLine,
        // which would inject Environment.NewLine = CRLF on Windows). LF-only
        // keeps managed sections byte-stable across hosts (Problem 1's hash
        // relies on this).
        var sb = new StringBuilder();
        sb.Append("<!-- step-section:").Append(stepName).Append(" -->\n");

        // Normalize any incoming CRLFs in agent content to LF too — the
        // agent might copy lines from a Windows host's task file. If the agent
        // sent body-only content, add a deterministic H2 for the section; if
        // it already supplied a heading, keep it as-is to avoid duplication.
        var trimmedContent = EnsureSectionHeading(
            stepName,
            (content ?? "").Replace("\r\n", "\n").TrimEnd('\n', ' ', '\t'));
        sb.Append(trimmedContent).Append('\n');

        if (openQuestions is { Count: > 0 })
        {
            sb.Append('\n');
            sb.Append(OpenQuestionsStart).Append('\n');
            sb.Append("### Open Questions\n");
            foreach (var q in openQuestions)
            {
                if (string.IsNullOrWhiteSpace(q)) continue;
                sb.Append("- ").Append(q.Trim()).Append('\n');
            }
            sb.Append(OpenQuestionsEnd).Append('\n');
        }

        if (resolvedDecisions is { Count: > 0 })
        {
            sb.Append('\n');
            sb.Append(ResolvedDecisionsStart).Append('\n');
            sb.Append("### Resolved Decisions\n");
            foreach (var d in resolvedDecisions)
            {
                if (string.IsNullOrWhiteSpace(d)) continue;
                sb.Append("- ").Append(d.Trim()).Append('\n');
            }
            sb.Append(ResolvedDecisionsEnd).Append('\n');
        }

        sb.Append("<!-- /step-section:").Append(stepName).Append(" -->");
        return sb.ToString();
    }

    private static string EnsureSectionHeading(string stepName, string content)
    {
        var trimmedStart = content.TrimStart('\n', ' ', '\t');
        if (MarkdownHeadingRegex().IsMatch(trimmedStart))
            return content;

        var title = PrettifyStepName(stepName);
        if (string.IsNullOrWhiteSpace(content))
            return $"## {title}";

        return $"## {title}\n\n{content.TrimStart('\n')}";
    }

    private static string PrettifyStepName(string stepName)
    {
        var name = stepName ?? "";
        const string optionalPrefix = "optional:";
        if (name.StartsWith(optionalPrefix, StringComparison.OrdinalIgnoreCase))
            name = name[optionalPrefix.Length..];

        name = Regex.Replace(name, @"[_\-:]+", " ").Trim();
        if (name.Length == 0)
            return "Step";

        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < words.Length; i++)
        {
            var word = words[i];
            words[i] = word.Length == 1
                ? word.ToUpperInvariant()
                : char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant();
        }
        return string.Join(' ', words);
    }

    [GeneratedRegex(@"^\s{0,3}#{1,6}\s+\S", RegexOptions.Multiline)]
    private static partial Regex MarkdownHeadingRegex();

    /// <summary>
    /// Recomposes the body. Initialises the managed-section markers when the
    /// split didn't find them (first write to a card).
    /// </summary>
    private static string Recompose(SplitResult split)
    {
        var sb = new StringBuilder();

        if (split.HadMarkers)
        {
            // SplitManagedBlock removed the start/end markers; restore them.
            sb.Append(split.Prefix);
            sb.Append(ManagedStart);
            sb.Append('\n');
            sb.Append(split.ManagedBlock.TrimStart('\n'));
        }
        else
        {
            // First write: prefix is the entire prior body, managedBlock is
            // the freshly-built section. Initialise markers at the end.
            sb.Append(split.Prefix.TrimEnd('\n', ' ', '\t'));
            if (sb.Length > 0)
                sb.Append("\n\n");
            sb.Append(ManagedStart);
            sb.Append('\n');
            sb.Append(split.ManagedBlock.TrimStart('\n'));
        }

        // Ensure exactly one blank line between content and end marker.
        if (!sb.ToString().EndsWith('\n'))
            sb.Append('\n');
        if (!sb.ToString().EndsWith("\n\n"))
            sb.Append('\n');
        sb.Append(ManagedEnd);
        sb.Append(split.Suffix);

        return sb.ToString();
    }
}
