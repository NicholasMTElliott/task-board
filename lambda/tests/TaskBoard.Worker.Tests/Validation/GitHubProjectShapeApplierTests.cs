using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Validation;
using TaskBoard.Worker.Tests.Helpers;

namespace TaskBoard.Worker.Tests.Validation;

/// <summary>
/// Pins gh-CLI invocation shape for <see cref="GitHubProjectShapeApplier"/>
/// using a stub <see cref="GitHubProjectShapeApplier.ProcessRunner"/>. Avoids
/// shelling out to a real gh executable so tests are deterministic.
/// </summary>
public class GitHubProjectShapeApplierTests
{
    private static GitHubProjectShapeApplier Build(
        GitHubProjectShapeApplier.ProcessRunner runner,
        string owner = "acme",
        string repo = "acme/widgets")
    {
        var opts = TestOptionsMonitor.Create(new GitHubProjectsOptions
        {
            Owner = owner,
            Repo = repo,
            ProjectNumber = "4",
        });
        return new GitHubProjectShapeApplier(opts, NullLogger<GitHubProjectShapeApplier>.Instance, runner);
    }

    [Fact]
    public async Task CreateField_BuildsCorrectGhArgs_OnSuccessReportsCreated()
    {
        IReadOnlyList<string>? captured = null;
        GitHubProjectShapeApplier.ProcessRunner runner = (cmd, args, _) =>
        {
            Assert.Equal("gh", cmd);
            captured = args.ToList();
            return Task.FromResult((0, "{\"id\":\"PVTSF_x\"}", ""));
        };

        var applier = Build(runner);
        var result = await applier.CreateSingleSelectFieldAsync(
            "4", "Activity", ["Design", "Implementation", "Review"], CancellationToken.None);

        Assert.Equal(ApplyOutcome.Created, result.Outcome);
        Assert.NotNull(captured);
        Assert.Contains("project", captured!);
        Assert.Contains("field-create", captured);
        Assert.Contains("4", captured);
        Assert.Contains("--owner", captured);
        Assert.Contains("acme", captured);
        Assert.Contains("--name", captured);
        Assert.Contains("Activity", captured);
        Assert.Contains("--data-type", captured);
        Assert.Contains("SINGLE_SELECT", captured);
        Assert.Contains("--single-select-options", captured);
        Assert.Contains("Design,Implementation,Review", captured);
    }

    [Fact]
    public async Task CreateField_OptionWithComma_FailsBeforeShellingOut()
    {
        var called = false;
        GitHubProjectShapeApplier.ProcessRunner runner = (_, _, _) =>
        {
            called = true;
            return Task.FromResult((0, "", ""));
        };

        var applier = Build(runner);
        var result = await applier.CreateSingleSelectFieldAsync(
            "4", "Bad", ["a,b", "c"], CancellationToken.None);

        Assert.Equal(ApplyOutcome.Failed, result.Outcome);
        Assert.False(called, "must not invoke gh — comma-in-option would silently mangle the option list");
        Assert.Contains("comma", result.Error ?? "");
    }

    [Fact]
    public async Task CreateField_OnAlreadyExistsStderr_ReturnsAlreadyExists()
    {
        // gh's actual output for duplicate field names contains "already exists".
        GitHubProjectShapeApplier.ProcessRunner runner = (_, _, _) =>
            Task.FromResult((1, "", "field with this name already exists\n"));

        var applier = Build(runner);
        var result = await applier.CreateSingleSelectFieldAsync(
            "4", "Activity", ["Design"], CancellationToken.None);

        Assert.Equal(ApplyOutcome.AlreadyExists, result.Outcome);
    }

    [Fact]
    public async Task CreateField_OnGenericFailure_ReportsErrorMessage()
    {
        GitHubProjectShapeApplier.ProcessRunner runner = (_, _, _) =>
            Task.FromResult((1, "", "auth required\n"));

        var applier = Build(runner);
        var result = await applier.CreateSingleSelectFieldAsync(
            "4", "Activity", ["Design"], CancellationToken.None);

        Assert.Equal(ApplyOutcome.Failed, result.Outcome);
        Assert.Contains("auth required", result.Error ?? "");
    }

    [Fact]
    public async Task CreateField_NoOptions_FailsLocally()
    {
        var applier = Build((_, _, _) => Task.FromResult((0, "", "")));
        var result = await applier.CreateSingleSelectFieldAsync(
            "4", "Activity", [], CancellationToken.None);

        Assert.Equal(ApplyOutcome.Failed, result.Outcome);
        Assert.Contains("at least one option", result.Error ?? "");
    }

    [Fact]
    public async Task CreateLabel_BuildsCorrectGhArgs_HandlesBareRepoForm()
    {
        IReadOnlyList<string>? captured = null;
        GitHubProjectShapeApplier.ProcessRunner runner = (_, args, _) =>
        {
            captured = args.ToList();
            return Task.FromResult((0, "", ""));
        };

        // Caller configured Repo as bare "widgets" (without owner/) — applier
        // should expand to "acme/widgets" before passing to gh.
        var applier = Build(runner, owner: "acme", repo: "widgets");
        var result = await applier.CreateLabelAsync("type:task", CancellationToken.None);

        Assert.Equal(ApplyOutcome.Created, result.Outcome);
        Assert.NotNull(captured);
        Assert.Contains("label", captured!);
        Assert.Contains("create", captured);
        Assert.Contains("type:task", captured);
        Assert.Contains("--repo", captured);
        Assert.Contains("acme/widgets", captured);
    }

    [Fact]
    public async Task CreateLabel_DuplicateAcceptedAsAlreadyExists()
    {
        // gh label create returns "Validation Failed: Name has already been taken"
        // on duplicate. The applier must classify that as AlreadyExists, not
        // Failed, so idempotent re-runs report cleanly.
        GitHubProjectShapeApplier.ProcessRunner runner = (_, _, _) =>
            Task.FromResult((1, "", "HTTP 422: Validation Failed (https://...)\nName has already been taken\n"));

        var applier = Build(runner);
        var result = await applier.CreateLabelAsync("type:task", CancellationToken.None);

        Assert.Equal(ApplyOutcome.AlreadyExists, result.Outcome);
    }

    [Fact]
    public async Task LooksLikeAlreadyExists_DoesNotMatchBareValidationFailed()
    {
        // Regression guard: the prior implementation matched "validation failed"
        // as already-exists, which would silently swallow a real failure ("invalid
        // option name", "missing required flag", etc). Pin the narrower match.
        GitHubProjectShapeApplier.ProcessRunner runner = (_, _, _) =>
            Task.FromResult((1, "", "HTTP 422: Validation Failed - color is invalid\n"));

        var applier = Build(runner);
        var result = await applier.CreateLabelAsync("oops", CancellationToken.None);

        Assert.Equal(ApplyOutcome.Failed, result.Outcome);
        Assert.Contains("color is invalid", result.Error ?? "");
    }

    [Fact]
    public async Task CreateField_GhMissingFromPath_ReturnsFailedNotCrash()
    {
        // gh not on PATH would normally throw Win32Exception/FileNotFoundException
        // out of Process.Start. The applier must catch and surface as a clean
        // Failed result so ApplyPlanAsync can move on to the next action.
        GitHubProjectShapeApplier.ProcessRunner runner = (_, _, _) =>
            throw new System.ComponentModel.Win32Exception(2, "The system cannot find the file specified");

        var applier = Build(runner);
        var result = await applier.CreateSingleSelectFieldAsync(
            "4", "Activity", ["Design"], CancellationToken.None);

        Assert.Equal(ApplyOutcome.Failed, result.Outcome);
        Assert.Contains("failed to invoke gh", result.Error ?? "");
    }

    [Fact]
    public async Task CreateLabel_GhMissingFromPath_ReturnsFailedNotCrash()
    {
        GitHubProjectShapeApplier.ProcessRunner runner = (_, _, _) =>
            throw new FileNotFoundException("gh executable not found");

        var applier = Build(runner);
        var result = await applier.CreateLabelAsync("type:task", CancellationToken.None);

        Assert.Equal(ApplyOutcome.Failed, result.Outcome);
        Assert.Contains("failed to invoke gh", result.Error ?? "");
    }

    [Fact]
    public async Task ProcessException_OperationCanceled_PropagatesNotSwallowed()
    {
        // Cancellation must propagate so callers can stop apply runs cleanly.
        // (We intentionally don't wrap OperationCanceledException as a Failed
        // result — that would mask graceful-shutdown intent.)
        GitHubProjectShapeApplier.ProcessRunner runner = (_, _, ct) =>
            throw new OperationCanceledException(ct);

        var applier = Build(runner);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            applier.CreateSingleSelectFieldAsync("4", "F", ["A"], cts.Token));
    }

    [Fact]
    public async Task CreateField_OwnerNotConfigured_FailsLocally()
    {
        var applier = Build(
            (_, _, _) => Task.FromResult((0, "", "")),
            owner: "");

        var result = await applier.CreateSingleSelectFieldAsync(
            "4", "Activity", ["Design"], CancellationToken.None);

        Assert.Equal(ApplyOutcome.Failed, result.Outcome);
        Assert.Contains("Owner", result.Error ?? "");
    }
}
