using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Models;
using TaskBoard.Worker.Processing;

namespace TaskBoard.Worker.Tests.Processing;

public class AgentRunnerClassifyFailureTests
{
    [Fact]
    public void ClassifyFailure_TimeoutException_ReturnsTimeout()
    {
        Assert.Equal(FailureReason.TIMEOUT,
            AgentRunner.ClassifyFailure(new TimeoutException("late")));
    }

    [Fact]
    public void ClassifyFailure_CliInfrastructureException_ReturnsInfrastructure()
    {
        Assert.Equal(FailureReason.INFRASTRUCTURE,
            AgentRunner.ClassifyFailure(new CliInfrastructureException("missing binary")));
    }

    [Fact]
    public void ClassifyFailure_GenericInvalidOperationException_ReturnsAgentError()
    {
        Assert.Equal(FailureReason.AGENT_ERROR,
            AgentRunner.ClassifyFailure(new InvalidOperationException("agent reported error")));
    }

    [Fact]
    public void ClassifyFailure_GenericException_ReturnsAgentError()
    {
        Assert.Equal(FailureReason.AGENT_ERROR,
            AgentRunner.ClassifyFailure(new Exception("other")));
    }

    [Fact]
    public void ClassifyFailure_CliInfrastructure_WinsOverInvalidOperationBaseType()
    {
        // CliInfrastructureException inherits from InvalidOperationException.
        // The pattern-match must classify by actual type, not base type.
        Exception ex = new CliInfrastructureException("docker 127");
        Assert.Equal(FailureReason.INFRASTRUCTURE, AgentRunner.ClassifyFailure(ex));
    }
}
