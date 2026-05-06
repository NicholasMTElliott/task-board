using System.Text.Json;
using Microsoft.Extensions.Options;

namespace TaskBoard.Worker.Clients;

public sealed class GitHubIssueDependencyClient(
    IOptions<GitHubProjectsOptions> options,
    ILogger<GitHubIssueDependencyClient> logger,
    ProcessRunnerDelegate? processRunner = null) : ICardDependencyClient
{
    private readonly GitHubProjectsOptions _options = options.Value;
    private readonly ProcessRunnerDelegate _runProcess = processRunner ?? ProcessRunner.RunProcessAsync;

    public async Task<IReadOnlyList<CardDependency>> GetBlockersAsync(
        string cardId, CancellationToken cancellationToken) =>
        await GetDependencyListAsync(cardId, "blocked_by", cancellationToken);

    public async Task<IReadOnlyList<CardDependency>> GetBlockedCardsAsync(
        string cardId, CancellationToken cancellationToken) =>
        await GetDependencyListAsync(cardId, "blocking", cancellationToken);

    public async Task AddBlockedByAsync(
        string blockedCardId, string blockerCardId, CancellationToken cancellationToken)
    {
        var blockerIssueId = await ResolveIssueDatabaseIdAsync(blockerCardId, cancellationToken);
        await RunGhAsync(
            [
                "api", "--method", "POST",
                $"repos/{_options.Repo}/issues/{blockedCardId}/dependencies/blocked_by",
                "-f", $"issue_id={blockerIssueId}"
            ],
            cancellationToken);
        logger.LogInformation("Added dependency: #{Blocked} is blocked by #{Blocker}", blockedCardId, blockerCardId);
    }

    public async Task RemoveBlockedByAsync(
        string blockedCardId, string blockerCardId, CancellationToken cancellationToken)
    {
        var blockerIssueId = await ResolveIssueDatabaseIdAsync(blockerCardId, cancellationToken);
        await RunGhAsync(
            [
                "api", "--method", "DELETE",
                $"repos/{_options.Repo}/issues/{blockedCardId}/dependencies/blocked_by/{blockerIssueId}"
            ],
            cancellationToken);
        logger.LogInformation("Removed dependency: #{Blocked} is blocked by #{Blocker}", blockedCardId, blockerCardId);
    }

    private async Task<IReadOnlyList<CardDependency>> GetDependencyListAsync(
        string cardId, string relation, CancellationToken ct)
    {
        var json = await RunGhAsync(
            ["api", $"repos/{_options.Repo}/issues/{cardId}/dependencies/{relation}"],
            ct);

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return [];

        var result = new List<CardDependency>();
        foreach (var issue in doc.RootElement.EnumerateArray())
        {
            var number = issue.TryGetProperty("number", out var numberProp)
                ? numberProp.GetInt32().ToString()
                : null;
            if (string.IsNullOrWhiteSpace(number))
                continue;

            var title = issue.TryGetProperty("title", out var titleProp)
                ? titleProp.GetString()
                : null;
            var state = issue.TryGetProperty("state", out var stateProp)
                ? stateProp.GetString()
                : null;
            var stateReason = issue.TryGetProperty("state_reason", out var stateReasonProp)
                ? stateReasonProp.GetString()
                : null;
            result.Add(new CardDependency(
                number,
                title,
                IsClosed: string.Equals(state, "closed", StringComparison.OrdinalIgnoreCase),
                StateReason: stateReason));
        }

        return result;
    }

    private async Task<long> ResolveIssueDatabaseIdAsync(string cardId, CancellationToken ct)
    {
        var json = await RunGhAsync(
            ["api", $"repos/{_options.Repo}/issues/{cardId}", "--jq", ".id"],
            ct);
        if (long.TryParse(json.Trim().Trim('"'), out var id))
            return id;

        throw new InvalidOperationException($"Could not resolve GitHub issue database id for #{cardId}.");
    }

    private async Task<string> RunGhAsync(string[] args, CancellationToken ct)
    {
        var (exitCode, stdout, stderr) = await _runProcess(
            "gh", args, Directory.GetCurrentDirectory(), 120, ct);
        if (exitCode != 0)
            throw new InvalidOperationException(
                $"gh exited with code {exitCode}. stderr: {stderr}");
        return stdout;
    }
}
