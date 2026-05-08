using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Unicode;
using TaskBoard.Worker.Clients;

namespace TaskBoard.Worker.Processing;

/// <summary>
/// Pure helpers for the rerun-redesign deterministic-skip cache (Problem 1).
/// Composes per-step input hashes from operator content + prior step outputs +
/// workflow / prompt configuration; computes section_output_hash from the
/// managed step section's normalized bytes.
///
/// <para>
/// Normalization rules (consistent across every input slot so two implementers
/// can't accidentally produce diverging hashes for the same logical state):
/// </para>
/// <list type="bullet">
///   <item>CRLF → LF</item>
///   <item>NFC Unicode normalization</item>
///   <item>Trailing whitespace stripped per line</item>
///   <item>Single trailing newline</item>
///   <item>UTF-8 bytes hashed with SHA-256, hex-lower output</item>
/// </list>
/// </summary>
internal static class RerunHashBuilder
{
    /// <summary>
    /// Computes the input-hash bundle for a step. Inputs concatenated in a
    /// fixed order with field separators so the hash is deterministic across
    /// hosts and reproducible from the same logical state.
    /// </summary>
    internal static string ComputeInputHash(InputHashInputs inputs)
    {
        var sb = new StringBuilder();

        sb.Append("operator_description:\n");
        sb.Append(Canonicalize(inputs.OperatorAuthoredDescription));
        sb.Append("\n---\n");

        sb.Append("operator_comments:\n");
        foreach (var c in inputs.OperatorComments)
        {
            sb.Append(Canonicalize(c));
            sb.Append("\n--\n");
        }
        sb.Append("\n---\n");

        sb.Append("prior_section_hashes:\n");
        foreach (var h in inputs.PriorSectionOutputHashes)
        {
            sb.Append(h.Trim().ToLowerInvariant());
            sb.Append('\n');
        }
        sb.Append("\n---\n");

        sb.Append("step_config:\n");
        sb.Append(Canonicalize(inputs.StepConfigJson));
        sb.Append("\n---\n");

        sb.Append("system_prompt:\n");
        sb.Append(Canonicalize(inputs.SystemPromptContents));
        sb.Append("\n---\n");

        sb.Append("task_prompt:\n");
        sb.Append(Canonicalize(inputs.TaskPromptContents));
        sb.Append("\n---\n");

        return Sha256Hex(sb.ToString());
    }

    /// <summary>
    /// Computes the section_output_hash for a step's managed section. Returns
    /// null when the section isn't present or is malformed (no closing
    /// marker) — the cache decision treats null as "section drifted, must
    /// re-run."
    /// </summary>
    internal static string? ComputeSectionHash(string body, string stepName)
    {
        if (string.IsNullOrEmpty(body) || string.IsNullOrWhiteSpace(stepName))
            return null;

        var open = $"<!-- step-section:{stepName} -->";
        var close = $"<!-- /step-section:{stepName} -->";

        var openIdx = body.IndexOf(open, StringComparison.Ordinal);
        if (openIdx < 0) return null;

        var contentStart = openIdx + open.Length;
        var closeIdx = body.IndexOf(close, contentStart, StringComparison.Ordinal);
        if (closeIdx < 0) return null;

        var content = body.Substring(contentStart, closeIdx - contentStart);
        return Sha256Hex(Canonicalize(content));
    }

    /// <summary>
    /// Normalize line endings, NFC-normalize, strip trailing whitespace per
    /// line, ensure exactly one trailing newline. Idempotent: applying twice
    /// equals applying once.
    /// </summary>
    internal static string Canonicalize(string? input)
    {
        if (string.IsNullOrEmpty(input))
            return "\n";

        // 1. Line-ending normalization
        var s = input.Replace("\r\n", "\n").Replace('\r', '\n');

        // 2. NFC normalization
        s = s.Normalize(NormalizationForm.FormC);

        // 3. Strip trailing whitespace per line
        var lines = s.Split('\n');
        for (int i = 0; i < lines.Length; i++)
            lines[i] = lines[i].TrimEnd(' ', '\t');
        s = string.Join('\n', lines);

        // 4. Ensure single trailing newline
        s = s.TrimEnd('\n') + "\n";

        return s;
    }

    private static string Sha256Hex(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        var hash = SHA256.HashData(bytes);
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash)
            sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        return sb.ToString();
    }
}

/// <summary>
/// Input bundle for <see cref="RerunHashBuilder.ComputeInputHash"/>. Each
/// field is canonicalized inside the builder; callers pass raw strings.
/// </summary>
internal sealed record InputHashInputs(
    string OperatorAuthoredDescription,
    IReadOnlyList<string> OperatorComments,
    IReadOnlyList<string> PriorSectionOutputHashes,
    string StepConfigJson,
    string SystemPromptContents,
    string TaskPromptContents);
