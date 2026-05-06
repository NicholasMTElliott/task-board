using System.Collections.Concurrent;
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

    // GitHub issue numeric → database id resolution is immutable for the lifetime
    // of an issue, so we cache it for the lifetime of this singleton client.
    // Keeps batch dependency-link writes from re-shelling `gh api` per blocker
    // when several new tickets reference the same existing card.
    private readonly ConcurrentDictionary<string, long> _databaseIdCache = new();

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
        var endpoint = $"repos/{_options.Repo}/issues/{cardId}/dependencies/{relation}";
        var json = await RunGhAsync(["api", endpoint], ct);

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new DependencyApiContractException(
                $"GitHub dependencies API returned non-JSON for {endpoint}. " +
                $"First 200 chars: {Truncate(json, 200)}", ex);
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                // The contract is a JSON array; anything else (object, error
                // payload, scalar) signals that GitHub changed the shape.
                // Fail loud so the operator notices and fixes the integration
                // rather than silently treating every blocked card as clear.
                throw new DependencyApiContractException(
                    $"GitHub dependencies API returned non-array response (kind={doc.RootElement.ValueKind}) " +
                    $"for {endpoint}. First 200 chars: {Truncate(json, 200)}");
            }

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
    }

    private async Task<long> ResolveIssueDatabaseIdAsync(string cardId, CancellationToken ct)
    {
        if (_databaseIdCache.TryGetValue(cardId, out var cached))
            return cached;

        var json = await RunGhAsync(
            ["api", $"repos/{_options.Repo}/issues/{cardId}", "--jq", ".id"],
            ct);
        if (long.TryParse(json.Trim().Trim('"'), out var id))
        {
            _databaseIdCache[cardId] = id;
            return id;
        }

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

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max];
}
