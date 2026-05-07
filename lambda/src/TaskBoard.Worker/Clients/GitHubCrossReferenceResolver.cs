using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using TaskBoard.Worker.Models;

namespace TaskBoard.Worker.Clients;

public sealed partial class GitHubCrossReferenceResolver(
    IOptionsMonitor<GitHubProjectsOptions> options,
    ILogger<GitHubCrossReferenceResolver> logger) : ICrossReferenceResolver
{
    private readonly GitHubProjectsOptions _options = options.CurrentValue;

    [GeneratedRegex(@"#(\d+)")]
    private static partial Regex IssueReferencePattern();

    public Task<IReadOnlyList<CardReference>> ParseTextReferencesAsync(
        string text, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(text))
            return Task.FromResult<IReadOnlyList<CardReference>>([]);

        var matches = IssueReferencePattern().Matches(text);
        var seen = new HashSet<string>();
        var refs = new List<CardReference>();

        foreach (Match match in matches)
        {
            var issueNumber = match.Groups[1].Value;
            if (seen.Add(issueNumber))
            {
                refs.Add(new CardReference(issueNumber, "mention", $"#{issueNumber}", null));
            }
        }

        return Task.FromResult<IReadOnlyList<CardReference>>(refs);
    }

    public async Task<IReadOnlyList<CardReference>> GetStructuredReferencesAsync(
        string cardId, CancellationToken cancellationToken)
    {
        try
        {
            var repoParts = _options.Repo.Split('/');
            var query = $$"""
                query {
                  repository(owner: "{{repoParts[0]}}", name: "{{repoParts[1]}}") {
                    issue(number: {{cardId}}) {
                      trackedIssues(first: 50) {
                        nodes { number title }
                      }
                      trackedInIssues(first: 10) {
                        nodes { number title }
                      }
                    }
                  }
                }
                """;

            var json = await RunGhAsync(["api", "graphql", "-f", $"query={query}"], cancellationToken);
            using var doc = JsonDocument.Parse(json);

            var issue = doc.RootElement
                .GetProperty("data").GetProperty("repository").GetProperty("issue");

            var refs = new List<CardReference>();

            if (issue.TryGetProperty("trackedIssues", out var tracked))
            {
                foreach (var node in tracked.GetProperty("nodes").EnumerateArray())
                {
                    var number = node.GetProperty("number").GetInt32().ToString();
                    var title = node.TryGetProperty("title", out var t) ? t.GetString() : null;
                    refs.Add(new CardReference(number, "sub_item", null, title));
                }
            }

            if (issue.TryGetProperty("trackedInIssues", out var trackedIn))
            {
                foreach (var node in trackedIn.GetProperty("nodes").EnumerateArray())
                {
                    var number = node.GetProperty("number").GetInt32().ToString();
                    var title = node.TryGetProperty("title", out var t) ? t.GetString() : null;
                    refs.Add(new CardReference(number, "parent_item", null, title));
                }
            }

            return refs;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to fetch structured references for card {CardId}", cardId);
            return [];
        }
    }

    private async Task<string> RunGhAsync(string[] args, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "gh",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        logger.LogDebug("Running: gh {Args}", string.Join(" ", args));

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start gh process");

        var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"gh exited with code {process.ExitCode}. stderr: {stderr}");
        }

        return stdout;
    }
}
