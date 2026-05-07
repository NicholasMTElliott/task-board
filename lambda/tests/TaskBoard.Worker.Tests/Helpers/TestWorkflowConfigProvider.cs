using Microsoft.Extensions.Logging.Abstractions;
using TaskBoard.Worker.Models;
using TaskBoard.Worker.Processing;

namespace TaskBoard.Worker.Tests.Helpers;

/// <summary>
/// Convenience factory for <see cref="WorkflowConfigProvider"/> in unit tests.
/// Wraps a static <see cref="WorkflowConfig"/>; file-watching is disabled
/// because the supplied path does not exist.
/// </summary>
public static class TestWorkflowConfigProvider
{
    public static WorkflowConfigProvider Create(WorkflowConfig config)
        => new(
            filePath: "nonexistent-test.json",
            promptBaseDir: string.Empty,
            initialConfig: config,
            logger: NullLogger<WorkflowConfigProvider>.Instance);
}
