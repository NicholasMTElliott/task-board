namespace TaskBoard.Worker.Processing;

/// <summary>
/// Parses the --since CLI flag value into a DateTimeOffset cutoff.
/// Format: &lt;N&gt;&lt;unit&gt; where unit is h (hours), d (days), or w (weeks).
/// Examples: "24h", "7d", "2w"
/// </summary>
public static class SinceParser
{
    /// <summary>
    /// Parses a since string and returns the cutoff DateTimeOffset relative to now.
    /// Returns null if the input is null, empty, or unparseable.
    /// </summary>
    public static DateTimeOffset? Parse(string? since)
    {
        if (string.IsNullOrWhiteSpace(since))
            return null;

        var span = since.Trim();
        if (span.Length < 2)
            return null;

        var unit = span[^1];
        if (!int.TryParse(span[..^1], out var value) || value <= 0)
            return null;

        return unit switch
        {
            'h' or 'H' => DateTimeOffset.UtcNow.AddHours(-value),
            'd' or 'D' => DateTimeOffset.UtcNow.AddDays(-value),
            'w' or 'W' => DateTimeOffset.UtcNow.AddDays(-(value * 7)),
            _ => null
        };
    }
}
