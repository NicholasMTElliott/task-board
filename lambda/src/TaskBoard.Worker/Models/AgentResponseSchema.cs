namespace TaskBoard.Worker.Models;

public static class AgentResponseSchema
{
    public const string JsonSchema = """
        {
          "type": "object",
          "properties": {
            "updates": {
              "type": "object",
              "additionalProperties": { "type": "string" }
            },
            "summaryComment": { "type": "string" },
            "outcome": {
              "type": "string",
              "enum": ["COMPLETE", "NEEDS_INFO", "BLOCKED"]
            },
            "approvalRequired": { "type": "boolean" }
          },
          "required": ["updates", "summaryComment", "outcome", "approvalRequired"]
        }
        """;

    public static string BuildSchemaInstruction() =>
        $"\n\nRespond ONLY with valid JSON matching this schema:\n{JsonSchema}";
}
