using System.Text.Json;

namespace TaskBoard.Worker.Models;

public static class AgentResponseParser
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static bool TryParse(string rawJson, out AgentResponse? response, out string? error)
    {
        response = null;
        error = null;

        try
        {
            response = JsonSerializer.Deserialize<AgentResponse>(rawJson, Options);
        }
        catch (JsonException ex)
        {
            error = $"Invalid JSON: {ex.Message}";
            return false;
        }

        if (response is null)
        {
            error = "Deserialization returned null.";
            return false;
        }

        if (response.Updates is null)
        {
            error = "Missing required field: updates.";
            response = null;
            return false;
        }

        if (response.Outcome is null)
        {
            error = "Missing required field: outcome.";
            response = null;
            return false;
        }

        if (response.SummaryComment is null)
        {
            error = "Missing required field: summaryComment.";
            response = null;
            return false;
        }

        return true;
    }
}
