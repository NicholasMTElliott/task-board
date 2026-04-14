using Microsoft.Extensions.Logging.Abstractions;
using TaskBoard.Worker.Models;
using TaskBoard.Worker.Validation;

namespace TaskBoard.Worker.Tests.Validation;

public class ValidationRunnerTests
{
    private sealed class FakeProbe(BoardShape? shape, Exception? ex = null) : IBoardShapeProbe
    {
        public Task<BoardShape?> ProbeAsync(string boardId, CancellationToken ct)
        {
            if (ex is not null) throw ex;
            return Task.FromResult(shape);
        }
    }

    private static WorkflowConfig ValidConfig() => new(
        States: new Dictionary<string, WorkflowState>
        {
            ["Ready"] = new("Ready", "ba", "agent_run", "p",
                new Dictionary<string, TransitionTarget>
                {
                    ["COMPLETE"] = TransitionTarget.ForColumn("Done"),
                },
                PipelineOrder: 1),
            ["Done"] = new("Done", null, "terminal", null, new Dictionary<string, TransitionTarget>()),
        },
        Roles: new Dictionary<string, WorkflowRole>
        {
            ["ba"] = new("m", "s", ["S"]),
        });

    [Fact]
    public async Task HappyPath_ReturnsZero()
    {
        var shape = new BoardShape(["Ready", "Done"], [], []);
        var runner = new ValidationRunner(
            ValidConfig(), new FakeProbe(shape), "github", "1", NullLogger<ValidationRunner>.Instance);

        var exit = await runner.RunAsync(CancellationToken.None);

        Assert.Equal(0, exit);
    }

    [Fact]
    public async Task StaticConfigErrors_CauseNonZeroExit()
    {
        var cfg = ValidConfig();
        cfg.States["Ready"] = cfg.States["Ready"] with { GateType = "bogus" };

        var runner = new ValidationRunner(
            cfg, new FakeProbe(new BoardShape(["Ready", "Done"], [], [])),
            "github", "1", NullLogger<ValidationRunner>.Instance);

        var exit = await runner.RunAsync(CancellationToken.None);

        Assert.Equal(1, exit);
    }

    [Fact]
    public async Task BoardColumnMismatch_CausesNonZeroExit()
    {
        // Board is missing the "Done" column
        var runner = new ValidationRunner(
            ValidConfig(), new FakeProbe(new BoardShape(["Ready"], [], [])),
            "github", "1", NullLogger<ValidationRunner>.Instance);

        var exit = await runner.RunAsync(CancellationToken.None);

        Assert.Equal(1, exit);
    }

    [Fact]
    public async Task ProbeReturnsNull_EmitsInfo_NotError()
    {
        // stub provider: probe can't introspect, should not fail the run
        var runner = new ValidationRunner(
            ValidConfig(), new FakeProbe(shape: null),
            "stub", "1", NullLogger<ValidationRunner>.Instance);

        var exit = await runner.RunAsync(CancellationToken.None);

        Assert.Equal(0, exit);
    }

    [Fact]
    public async Task ProbeThrows_EmitsWarning_StaticChecksStillRun()
    {
        var cfg = ValidConfig();
        cfg.States["Ready"] = cfg.States["Ready"] with { GateType = "bogus" };

        var runner = new ValidationRunner(
            cfg, new FakeProbe(null, new InvalidOperationException("auth")),
            "github", "1", NullLogger<ValidationRunner>.Instance);

        var exit = await runner.RunAsync(CancellationToken.None);

        // Static error still fails the run
        Assert.Equal(1, exit);
    }

    [Fact]
    public async Task MissingPromptFile_CausesNonZeroExit()
    {
        var cfg = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["Ready"] = new("Ready", "ba", "agent_run", null,
                    new Dictionary<string, TransitionTarget>
                    {
                        ["COMPLETE"] = TransitionTarget.ForColumn("Done"),
                    },
                    PipelineOrder: 1,
                    Steps: [new WorkflowStep("impl", "ba", TaskPromptFile: "prompts/does-not-exist.md")]),
                ["Done"] = new("Done", null, "terminal", null, new Dictionary<string, TransitionTarget>()),
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["ba"] = new("m", "s", ["S"]),
            })
        {
            ConfigDirectory = Path.GetTempPath(),
        };

        var runner = new ValidationRunner(
            cfg, new FakeProbe(new BoardShape(["Ready", "Done"], [], [])),
            "github", "1", NullLogger<ValidationRunner>.Instance);

        var exit = await runner.RunAsync(CancellationToken.None);

        Assert.Equal(1, exit);
    }
}
