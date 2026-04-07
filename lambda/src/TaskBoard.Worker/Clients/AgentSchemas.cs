namespace TaskBoard.Worker.Clients;

/// <summary>
/// Shared JSON schema constants used by agent executors for structured output.
/// </summary>
internal static class AgentSchemas
{
    /// <summary>
    /// JSON Schema for the structured agent outcome response.
    /// Used by <see cref="ClaudeAgentExecutor"/> (Claude's --json-schema).
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
            },
            "estimate": {
              "type": "number"
            }
          },
          "required": ["outcome"]
        }
        """;

    /// <summary>
    /// OpenAI-compatible variant of <see cref="OutcomeSchema"/>.
    /// Used by <see cref="CodexAgentExecutor"/> (codex --output-schema).
    /// OpenAI structured outputs require:
    ///   - "additionalProperties": false on every object
    ///   - All properties listed in "required"
    ///   - Optional fields use ["type", "null"] instead of being omitted from required
    /// </summary>
    internal const string OutcomeSchemaOpenAI = """
        {
          "type": "object",
          "properties": {
            "outcome": {
              "type": "string",
              "enum": ["COMPLETE", "NEEDS_INFO", "ERROR"]
            },
            "detail": {
              "type": ["string", "null"]
            },
            "questions": {
              "type": ["array", "null"],
              "items": {
                "type": "object",
                "properties": {
                  "question": { "type": "string" },
                  "recommendations": {
                    "type": ["array", "null"],
                    "items": { "type": "string" }
                  }
                },
                "required": ["question", "recommendations"],
                "additionalProperties": false
              }
            },
            "requestedSteps": {
              "type": ["array", "null"],
              "items": { "type": "string" }
            },
            "estimate": {
              "type": ["number", "null"]
            }
          },
          "required": ["outcome", "detail", "questions", "requestedSteps", "estimate"],
          "additionalProperties": false
        }
        """;
}
