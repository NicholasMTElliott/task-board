using System.Security.Cryptography;
using System.Text;

namespace TaskBoard.Worker.Clients;

/// <summary>
/// Identifies the tenant (project/board) this worker process is operating against.
/// Format: "{provider}:{provider-specific-identifier}", e.g. "github:owner/repo/4".
/// Stored on every per-tenant DB row and used to scope queries.
/// </summary>
public interface ITenantIdentifier
{
    /// <summary>Full canonical tenant string, e.g. "github:owner/repo/4".</summary>
    string Value { get; }

    /// <summary>Provider prefix, e.g. "github", "trello", "stub".</summary>
    string Provider { get; }

    /// <summary>
    /// Stable 8-char hex hash of <see cref="Value"/>. Used in places where the
    /// full tenant string is too long or character-restricted (Docker container
    /// names, PGMQ queue names).
    /// </summary>
    string ShortHash { get; }
}

internal sealed class TenantIdentifier : ITenantIdentifier
{
    public TenantIdentifier(string provider, string identifier)
    {
        if (string.IsNullOrWhiteSpace(provider))
            throw new ArgumentException("Tenant provider must be non-empty.", nameof(provider));
        if (string.IsNullOrWhiteSpace(identifier))
            throw new ArgumentException("Tenant identifier must be non-empty.", nameof(identifier));

        Provider = provider;
        Value = $"{provider}:{identifier}";
        ShortHash = ComputeShortHash(Value);
    }

    public string Value { get; }
    public string Provider { get; }
    public string ShortHash { get; }

    private static string ComputeShortHash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        var sb = new StringBuilder(8);
        for (var i = 0; i < 4; i++)
            sb.Append(bytes[i].ToString("x2"));
        return sb.ToString();
    }
}

/// <summary>
/// Resolves an <see cref="ITenantIdentifier"/> from the merged configuration.
/// Throws on missing/invalid config — callers should let this propagate so the
/// process fails fast at startup rather than writing untenanted rows.
/// </summary>
public static class TenantIdentifierFactory
{
    public static ITenantIdentifier Create(
        string boardProvider,
        GitHubProjectsOptions? githubOptions,
        TrelloClientOptions? trelloOptions,
        string? stubTenantName)
    {
        switch (boardProvider)
        {
            case "github":
                if (githubOptions is null)
                    throw new InvalidOperationException(
                        "BoardProvider=github but GitHubProjectsOptions is unavailable.");
                Require(githubOptions.Owner, "GitHubProjects:Owner");
                Require(githubOptions.Repo, "GitHubProjects:Repo");
                Require(githubOptions.ProjectNumber, "GitHubProjects:ProjectNumber");

                var repo = githubOptions.Repo.Contains('/')
                    ? githubOptions.Repo.Split('/', 2)[1]
                    : githubOptions.Repo;
                return new TenantIdentifier(
                    "github",
                    $"{githubOptions.Owner}/{repo}/{githubOptions.ProjectNumber}");

            case "trello":
            case "live":
                if (trelloOptions is null)
                    throw new InvalidOperationException(
                        "BoardProvider=trello but TrelloClientOptions is unavailable.");
                Require(trelloOptions.BoardId, "Trello:BoardId");
                return new TenantIdentifier("trello", trelloOptions.BoardId);

            case "stub":
            default:
                return new TenantIdentifier("stub", stubTenantName ?? "test");
        }
    }

    private static void Require(string value, string configKey)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException(
                $"Tenant identifier cannot be resolved: required config '{configKey}' is missing or empty. " +
                "Set it in appsettings.json, .aiboard/appsettings.json, an env var, or a CLI flag.");
    }
}
