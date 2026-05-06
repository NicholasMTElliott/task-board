using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TaskBoard.Worker.Clients;

namespace TaskBoard.Worker.Tests.Clients;

public class GitHubIssueDependencyClientTests
{
    private static GitHubProjectsOptions GitHubOptions() => new()
    {
        Owner = "octo",
        Repo = "octo/repo",
        ProjectNumber = "1",
    };

    [Fact]
    public async Task GetBlockersAsync_UsesBlockedByEndpoint()
    {
        var calls = new List<string[]>();
        ProcessRunnerDelegate runner = (exe, args, wd, timeout, ct, stdin, remove, agent, inactivity) =>
        {
            calls.Add(args);
            return Task.FromResult((0, """
                [
                  { "number": 5, "title": "Create database", "state": "open" }
                ]
                """, ""));
        };

        var client = new GitHubIssueDependencyClient(
            Microsoft.Extensions.Options.Options.Create(GitHubOptions()),
            NullLogger<GitHubIssueDependencyClient>.Instance,
            runner);

        var blockers = await client.GetBlockersAsync("10", CancellationToken.None);

        Assert.Single(blockers);
        Assert.Equal("5", blockers[0].CardId);
        Assert.Contains("repos/octo/repo/issues/10/dependencies/blocked_by", calls[0]);
    }

    [Fact]
    public async Task AddBlockedByAsync_ResolvesIssueIdThenPostsDependency()
    {
        var calls = new List<string[]>();
        ProcessRunnerDelegate runner = (exe, args, wd, timeout, ct, stdin, remove, agent, inactivity) =>
        {
            calls.Add(args);
            if (args.Contains("--jq"))
                return Task.FromResult((0, "12345\n", ""));
            return Task.FromResult((0, "{}", ""));
        };

        var client = new GitHubIssueDependencyClient(
            Options.Create(GitHubOptions()),
            NullLogger<GitHubIssueDependencyClient>.Instance,
            runner);

        await client.AddBlockedByAsync("10", "5", CancellationToken.None);

        Assert.Contains("repos/octo/repo/issues/5", calls[0]);
        Assert.Contains("--method", calls[1]);
        Assert.Contains("POST", calls[1]);
        Assert.Contains("repos/octo/repo/issues/10/dependencies/blocked_by", calls[1]);
        Assert.Contains("issue_id=12345", calls[1]);
    }
}
