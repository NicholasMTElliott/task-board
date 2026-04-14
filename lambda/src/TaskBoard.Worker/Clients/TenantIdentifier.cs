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
            {
                var missing = new List<string>();
                if (githubOptions is null || string.IsNullOrWhiteSpace(githubOptions.Owner))
                    missing.Add("GitHubProjects:Owner");
                if (githubOptions is null || string.IsNullOrWhiteSpace(githubOptions.Repo))
                    missing.Add("GitHubProjects:Repo");
                if (githubOptions is null || string.IsNullOrWhiteSpace(githubOptions.ProjectNumber))
                    missing.Add("GitHubProjects:ProjectNumber");
                if (missing.Count > 0)
                    throw Missing("BoardProvider=github", missing);

                var repo = githubOptions!.Repo.Contains('/')
                    ? githubOptions.Repo.Split('/', 2)[1]
                    : githubOptions.Repo;
                return new TenantIdentifier(
                    "github",
                    $"{githubOptions.Owner}/{repo}/{githubOptions.ProjectNumber}");
            }

            case "trello":
            case "live":
            {
                var missing = new List<string>();
                if (trelloOptions is null || string.IsNullOrWhiteSpace(trelloOptions.BoardId))
                    missing.Add("Trello:BoardId");
                if (missing.Count > 0)
                    throw Missing($"BoardProvider={boardProvider}", missing);

                return new TenantIdentifier("trello", trelloOptions!.BoardId);
            }

            case "stub":
            default:
                return new TenantIdentifier("stub", stubTenantName ?? "test");
        }
    }

    private static InvalidOperationException Missing(string context, IReadOnlyList<string> keys) =>
        new($"Tenant identifier cannot be resolved: {context} but required config " +
            $"{(keys.Count == 1 ? $"key '{keys[0]}' is" : $"keys [{string.Join(", ", keys)}] are")} " +
            "missing or empty. Set in appsettings.json, .aiboard/appsettings.json, an env var, or a CLI flag.");
}
