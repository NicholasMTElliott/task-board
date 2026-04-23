namespace TaskBoard.Worker.Clients;

/// <summary>
/// Thrown by an agent executor when the failure is attributable to the CLI
/// runtime or container plumbing (binary missing, permission denied, Docker
/// daemon error, etc.) rather than to the agent's logical output.
/// </summary>
/// <remarks>
/// <see cref="Processing.AgentRunner"/> classifies this as
/// <see cref="Models.FailureReason.INFRASTRUCTURE"/> when recording the run.
/// Subclasses <see cref="InvalidOperationException"/> for backward compatibility
/// with existing <c>catch (InvalidOperationException)</c> sites.
/// </remarks>
public sealed class CliInfrastructureException : InvalidOperationException
{
    public CliInfrastructureException(string message) : base(message) { }

    public CliInfrastructureException(string message, Exception innerException)
        : base(message, innerException) { }
}
