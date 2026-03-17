using TaskBoard.Worker.Models;

namespace TaskBoard.Worker.Clients;

public sealed class StubLlmClient(ILogger<StubLlmClient> logger) : ILlmClient
{
    public Task<AgentResponse> GetCompletionAsync(
        string model,
        string systemPrompt,
        string userPrompt,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("[Stub] LLM call model={Model} systemPrompt={SystemLen} chars, userPrompt={UserLen} chars",
            model, systemPrompt.Length, userPrompt.Length);

        var response = new AgentResponse(
            Updates: new Dictionary<string, string>
            {
                ["Requirements"] = "Stub: requirements produced by agent.",
                ["Open Questions"] = "Stub: no open questions.",
                ["Acceptance Criteria"] = "Stub: acceptance criteria defined.",
                ["Technical Design"] = "Stub: technical design produced.",
                ["Decisions"] = "Stub: key decisions documented.",
                ["Test Plan"] = "Stub: test plan generated."
            },
            SummaryComment: "<!-- agent-status -->\n**Agent run complete (stub).** All sections updated with placeholder content.",
            Outcome: "COMPLETE",
            ApprovalRequired: false);

        return Task.FromResult(response);
    }
}
