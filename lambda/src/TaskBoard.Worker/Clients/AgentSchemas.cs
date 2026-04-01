namespace TaskBoard.Worker.Clients;

/// <summary>
/// Shared JSON schema constants used by agent executors for structured output.
/// </summary>
internal static class AgentSchemas
{
    /// <summary>
    /// JSON Schema for the structured agent outcome response.
    /// Used by both <see cref="ClaudeAgentExecutor"/> and <see cref="CodexAgentExecutor"/>.
    /// </summary>
    internal const string OutcomeSchema = """
        {
          "type": "object",
          "properties": {
            "outcome": {
              "type": "string",
              "enum": ["COMPLETE", "NEEDS_INFO", "ERROR"]
            },
            "detail": {
              "type": "string"
            },
            "questions": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "question": { "type": "string" },
                  "recommendations": {
                    "type": "array",
                    "items": { "type": "string" }
                  }
                },
                "required": ["question"]
              }
            },
            "requestedSteps": {
              "type": "array",
              "items": { "type": "string" }
            }
          },
          "required": ["outcome"]
        }
        """;
}
