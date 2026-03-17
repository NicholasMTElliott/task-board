namespace TaskBoard.Worker.Models;

public sealed record AgentResponse(
    Dictionary<string, string> Updates,
    string SummaryComment,
    string Outcome,
    bool ApprovalRequired);
