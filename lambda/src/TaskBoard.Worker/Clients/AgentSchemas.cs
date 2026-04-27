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

    /// <summary>
    /// Evaluator-specific schema (Claude / generic JSON Schema).
    /// Extends <see cref="OutcomeSchema"/> with <c>winner_index</c> + <c>scores</c>.
    /// <c>winner_index</c> is always required and typed <c>["integer", "null"]</c>
    /// — a strictly stronger constraint than the prior <c>if</c>/<c>then</c>
    /// formulation, which Claude CLI's <c>--json-schema</c> treated as a soft
    /// hint rather than enforcing at the wire level (KvA v0.0.16 field report).
    /// "Always required" is reliably honoured across providers; "required when X"
    /// is not. Parser-side defense in
    /// <see cref="TaskBoard.Worker.Processing.CandidateExecutor"/> rejects
    /// <c>outcome=COMPLETE</c> with a null <c>winner_index</c>.
    /// </summary>
    internal const string EvaluatorOutcomeSchema = """
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
            "winner_index": {
              "type": ["integer", "null"]
            },
            "scores": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "index": { "type": "integer", "minimum": 0 },
                  "score": { "type": "number" },
                  "reasoning": { "type": "string" }
                },
                "required": ["index"]
              }
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
          "required": ["outcome", "winner_index"]
        }
        """;

    /// <summary>
    /// OpenAI-compatible variant of <see cref="EvaluatorOutcomeSchema"/>.
    /// OpenAI structured outputs do NOT support the <c>if</c>/<c>then</c>
    /// keywords, so <c>winner_index</c> is always present but typed
    /// <c>["integer", "null"]</c>; parser-side enforcement (in
    /// <see cref="TaskBoard.Worker.Processing.CandidateExecutor"/>) rejects a
    /// COMPLETE outcome with a null <c>winner_index</c>.
    /// </summary>
    internal const string EvaluatorOutcomeSchemaOpenAI = """
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
            "winner_index": {
              "type": ["integer", "null"]
            },
            "scores": {
              "type": ["array", "null"],
              "items": {
                "type": "object",
                "properties": {
                  "index": { "type": "integer" },
                  "score": { "type": "number" },
                  "reasoning": { "type": ["string", "null"] }
                },
                "required": ["index", "score", "reasoning"],
                "additionalProperties": false
              }
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
            }
          },
          "required": ["outcome", "detail", "winner_index", "scores", "questions", "requestedSteps"],
          "additionalProperties": false
        }
        """;
}
