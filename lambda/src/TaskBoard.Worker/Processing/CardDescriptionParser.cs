using System.Text.RegularExpressions;

namespace TaskBoard.Worker.Processing;

public static partial class CardDescriptionParser
{
    [GeneratedRegex(@"^# ", RegexOptions.Multiline)]
    private static partial Regex SectionHeadingRegex();

    public static Dictionary<string, string> ParseSections(string description)
    {
        var sections = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(description))
            return sections;

        var parts = SectionHeadingRegex().Split(description);

        // parts[0] is text before the first "# " heading (preamble) — ignored
        for (var i = 1; i < parts.Length; i++)
        {
            var newlineIndex = parts[i].IndexOf('\n');
            if (newlineIndex < 0)
            {
                sections[parts[i].Trim()] = string.Empty;
            }
            else
            {
                var heading = parts[i][..newlineIndex].Trim();
                var body = parts[i][(newlineIndex + 1)..].Trim();
                sections[heading] = body;
            }
        }

        return sections;
    }

    public static string MergeSections(string existingDescription, Dictionary<string, string> updates, List<string> allowedSections)
    {
        var existing = ParseSections(existingDescription);

        foreach (var (key, value) in updates)
        {
            if (allowedSections.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                existing[key] = value;
            }
        }

        return string.Join("\n\n", existing.Select(kvp => $"# {kvp.Key}\n{kvp.Value}"));
    }
}
