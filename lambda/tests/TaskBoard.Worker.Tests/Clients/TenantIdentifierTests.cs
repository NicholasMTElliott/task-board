using TaskBoard.Worker.Clients;

namespace TaskBoard.Worker.Tests.Clients;

public class TenantIdentifierTests
{
    // ── GitHub provider ─────────────────────────────────────────────────────

    [Fact]
    public void Create_GitHub_ProducesProviderPrefixedValue()
    {
        var tenant = TenantIdentifierFactory.Create(
            "github",
            new GitHubProjectsOptions { Owner = "octo", Repo = "octo/widgets", ProjectNumber = "4" },
            trelloOptions: null,
            stubTenantName: null);

        Assert.Equal("github", tenant.Provider);
        Assert.Equal("github:octo/widgets/4", tenant.Value);
    }

    [Fact]
    public void Create_GitHub_AcceptsBareRepoName()
    {
        // Repo can be "owner/repo" (canonical) or just "repo" — both yield the same tenant.
        var tenant = TenantIdentifierFactory.Create(
            "github",
            new GitHubProjectsOptions { Owner = "octo", Repo = "widgets", ProjectNumber = "4" },
            trelloOptions: null,
            stubTenantName: null);

        Assert.Equal("github:octo/widgets/4", tenant.Value);
    }

    [Theory]
    [InlineData("", "repo", "1", "Owner")]
    [InlineData("owner", "", "1", "Repo")]
    [InlineData("owner", "repo", "", "ProjectNumber")]
    public void Create_GitHub_FailsFastWhenRequiredFieldMissing(
        string owner, string repo, string projectNumber, string missingFieldHint)
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            TenantIdentifierFactory.Create(
                "github",
                new GitHubProjectsOptions { Owner = owner, Repo = repo, ProjectNumber = projectNumber },
                trelloOptions: null,
                stubTenantName: null));

        // Error message must name the offending config key so operators can fix it.
        Assert.Contains(missingFieldHint, ex.Message);
    }

    [Fact]
    public void Create_GitHub_FailsWhenOptionsNull()
    {
        Assert.Throws<InvalidOperationException>(() =>
            TenantIdentifierFactory.Create("github", githubOptions: null, trelloOptions: null, stubTenantName: null));
    }

    // ── Trello provider ─────────────────────────────────────────────────────

    [Fact]
    public void Create_Trello_ProducesProviderPrefixedValue()
    {
        var tenant = TenantIdentifierFactory.Create(
            "trello",
            githubOptions: null,
            new TrelloClientOptions { BoardId = "abc123" },
            stubTenantName: null);

        Assert.Equal("trello", tenant.Provider);
        Assert.Equal("trello:abc123", tenant.Value);
    }

    [Fact]
    public void Create_Trello_FailsFastWhenBoardIdMissing()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            TenantIdentifierFactory.Create(
                "trello",
                githubOptions: null,
                new TrelloClientOptions { BoardId = "" },
                stubTenantName: null));

        Assert.Contains("Trello:BoardId", ex.Message);
    }

    // ── Stub provider ───────────────────────────────────────────────────────

    [Fact]
    public void Create_Stub_DefaultsToTestTenant()
    {
        var tenant = TenantIdentifierFactory.Create(
            "stub", githubOptions: null, trelloOptions: null, stubTenantName: null);

        Assert.Equal("stub", tenant.Provider);
        Assert.Equal("stub:test", tenant.Value);
    }

    [Fact]
    public void Create_Stub_HonorsConfiguredName()
    {
        var tenant = TenantIdentifierFactory.Create(
            "stub", githubOptions: null, trelloOptions: null, stubTenantName: "project-a");

        Assert.Equal("stub:project-a", tenant.Value);
    }

    [Fact]
    public void Create_UnknownProvider_FallsBackToStub()
    {
        // Defensive default: an unfamiliar provider name should produce a stub tenant
        // rather than throwing — actual board provider validation happens elsewhere.
        var tenant = TenantIdentifierFactory.Create(
            "future-provider", githubOptions: null, trelloOptions: null, stubTenantName: "fallback");

        Assert.Equal("stub", tenant.Provider);
        Assert.Equal("stub:fallback", tenant.Value);
    }

    // ── ShortHash ───────────────────────────────────────────────────────────

    [Fact]
    public void ShortHash_IsStableForSameValue()
    {
        var t1 = TenantIdentifierFactory.Create(
            "stub", githubOptions: null, trelloOptions: null, stubTenantName: "project-a");
        var t2 = TenantIdentifierFactory.Create(
            "stub", githubOptions: null, trelloOptions: null, stubTenantName: "project-a");

        Assert.Equal(t1.ShortHash, t2.ShortHash);
    }

    [Fact]
    public void ShortHash_DiffersAcrossDistinctTenants()
    {
        // Critical for Docker container naming: two tenants sharing a card_id must
        // produce different container names so concurrent runs don't collide.
        var a = TenantIdentifierFactory.Create(
            "github",
            new GitHubProjectsOptions { Owner = "octo", Repo = "alpha", ProjectNumber = "1" },
            trelloOptions: null, stubTenantName: null);
        var b = TenantIdentifierFactory.Create(
            "github",
            new GitHubProjectsOptions { Owner = "octo", Repo = "beta", ProjectNumber = "1" },
            trelloOptions: null, stubTenantName: null);

        Assert.NotEqual(a.ShortHash, b.ShortHash);
    }

    [Fact]
    public void ShortHash_IsLowerHexAndFitsContainerNames()
    {
        // Docker container names allow [a-zA-Z0-9_.-]; the hash must be safe to embed.
        var tenant = TenantIdentifierFactory.Create(
            "github",
            new GitHubProjectsOptions { Owner = "octo", Repo = "widgets", ProjectNumber = "4" },
            trelloOptions: null, stubTenantName: null);

        Assert.Equal(8, tenant.ShortHash.Length);
        Assert.Matches("^[0-9a-f]{8}$", tenant.ShortHash);
    }
}
