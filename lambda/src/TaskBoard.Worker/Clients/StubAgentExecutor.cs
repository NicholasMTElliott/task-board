namespace TaskBoard.Worker.Clients;

public sealed class StubAgentExecutor(ILogger<StubAgentExecutor> logger) : IAgentExecutor
{
    public AgentOutcome NextOutcome { get; set; } = AgentOutcome.SUCCESS;
    public string DesignContent { get; set; } = "## Technical Design\n\nStub technical design produced by agent.";

    public async Task<AgentOutcome> ExecuteAsync(
        AgentExecutionContext context, CancellationToken cancellationToken)
    {
        logger.LogInformation("[Stub] Agent executing for card {CardId} in {Workspace}",
            context.TargetCardId, context.WorkspacePath);

        // Simulate agent modifying the target task file
        var taskFilePath = Processing.TaskFileManager.GetTaskFilePath(
            context.WorkspacePath, context.TargetCardId);

        if (File.Exists(taskFilePath))
        {
            var existing = await File.ReadAllTextAsync(taskFilePath, cancellationToken);

            var appendContent = NextOutcome switch
            {
                AgentOutcome.SUCCESS => $"\n\n{DesignContent}",
                AgentOutcome.QUESTIONS => "\n\n## Questions\n\n- What is the expected scale?\n- Are there existing patterns to follow?",
                AgentOutcome.ERROR => "\n\n## Error\n\nStub error: simulated failure during agent execution.",
                _ => ""
            };

            await File.WriteAllTextAsync(taskFilePath, existing + appendContent, cancellationToken);
        }

        logger.LogInformation("[Stub] Agent complete, outcome={Outcome}", NextOutcome);
        return NextOutcome;
    }
}
