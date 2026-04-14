using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TaskBoard.Worker.Clients;

namespace TaskBoard.Worker.Validation;

/// <summary>
/// Introspects a GitHub Projects v2 board via the <c>gh</c> CLI:
/// project fields + single-select options, and repository labels.
/// </summary>
public sealed class GitHubProjectShapeProbe(
    IOptions<GitHubProjectsOptions> options,
    ILogger<GitHubProjectShapeProbe> logger) : IBoardShapeProbe
{
    private readonly GitHubProjectsOptions _options = options.Value;

    public async Task<BoardShape?> ProbeAsync(string boardId, CancellationToken ct)
    {
        var fields = await FetchProjectFieldsAsync(boardId, ct);
        var statusField = fields.FirstOrDefault(f =>
            string.Equals(f.Name, _options.StatusFieldName, StringComparison.OrdinalIgnoreCase));
        var columns = statusField?.Options?.Select(o => o.Name).ToList()
                      ?? new List<string>();

        var labels = await FetchLabelsAsync(ct);

        return new BoardShape(columns, fields, labels);
    }

    private async Task<IReadOnlyList<BoardField>> FetchProjectFieldsAsync(string projectNumber, CancellationToken ct)
    {
        // `gh project field-list <number> --owner <owner> --format json` returns
        // { "fields": [ { "id", "name", "type", "options"?: [ { "id", "name" } ] } ] }
        var json = await RunGhAsync(
            ["project", "field-list", projectNumber, "--owner", _options.Owner, "--format", "json", "--limit", "100"],
            ct);

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("fields", out var fieldsProp))
            return Array.Empty<BoardField>();

        var results = new List<BoardField>();
        foreach (var f in fieldsProp.EnumerateArray())
        {
            var name = f.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var dataType = f.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(name)) continue;

            List<BoardFieldOption>? options = null;
            if (f.TryGetProperty("options", out var optsProp) && optsProp.ValueKind == JsonValueKind.Array)
            {
                options = new List<BoardFieldOption>();
                foreach (var o in optsProp.EnumerateArray())
                {
                    var optName = o.TryGetProperty("name", out var on) ? on.GetString() ?? "" : "";
                    var optId = o.TryGetProperty("id", out var oi) ? oi.GetString() ?? "" : "";
                    if (!string.IsNullOrEmpty(optName))
                        options.Add(new BoardFieldOption(optName, optId));
                }
            }

            results.Add(new BoardField(name, dataType, options));
        }
        return results;
    }

    private async Task<IReadOnlyList<string>> FetchLabelsAsync(CancellationToken ct)
    {
        // Accept "owner/repo" or bare "repo"
        var repoSlug = _options.Repo.Contains('/')
            ? _options.Repo
            : $"{_options.Owner}/{_options.Repo}";

        try
        {
            var json = await RunGhAsync(
                ["label", "list", "--repo", repoSlug, "--limit", "200", "--json", "name"],
                ct);
            using var doc = JsonDocument.Parse(json);
            var labels = new List<string>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.TryGetProperty("name", out var n) && n.GetString() is { } s)
                    labels.Add(s);
            }
            return labels;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to enumerate labels for {Repo}; label checks will be skipped.", repoSlug);
            return Array.Empty<string>();
        }
    }

    private async Task<string> RunGhAsync(string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "gh",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        logger.LogDebug("Running: gh {Args}", string.Join(" ", args));

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start gh process");

        var stdout = await process.StandardOutput.ReadToEndAsync(ct);
        var stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"gh {string.Join(" ", args)} failed (exit {process.ExitCode}): {stderr}");

        return stdout;
    }
}
