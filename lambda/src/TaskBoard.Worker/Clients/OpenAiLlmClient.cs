using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TaskBoard.Worker.Models;

namespace TaskBoard.Worker.Clients;

public sealed class OpenAiLlmClient(
    HttpClient httpClient,
    IOptions<OpenAiLlmOptions> options,
    ILogger<OpenAiLlmClient> logger) : ILlmClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly HttpClient _httpClient = httpClient;
    private readonly OpenAiLlmOptions _options = options.Value;
    private readonly ILogger<OpenAiLlmClient> _logger = logger;

    public async Task<AgentResponse> GetCompletionAsync(
        string model, string systemPrompt, string userPrompt, CancellationToken cancellationToken)
    {
        var fullSystemPrompt = systemPrompt + AgentResponseSchema.BuildSchemaInstruction();

        var requestBody = new
        {
            model,
            response_format = new { type = "json_object" },
            messages = new object[]
            {
                new { role = "system", content = fullSystemPrompt },
                new { role = "user", content = userPrompt }
            }
        };

        var json = JsonSerializer.Serialize(requestBody, JsonOptions);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions");
        request.Content = content;
        request.Headers.Add("Authorization", $"Bearer {_options.ApiKey}");

        _logger.LogInformation("Calling OpenAI API model={Model}", model);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new LlmApiException("openai",
                $"API returned {response.StatusCode}: {responseBody}");
        }

        var contentText = ExtractContentText(responseBody);

        if (!AgentResponseParser.TryParse(contentText, out var agentResponse, out var error))
        {
            throw new LlmApiException("openai", $"Failed to parse agent response: {error}");
        }

        _logger.LogInformation("OpenAI API call complete, outcome={Outcome}", agentResponse!.Outcome);
        return agentResponse;
    }

    internal static string ExtractContentText(string responseJson)
    {
        using var doc = JsonDocument.Parse(responseJson);
        var root = doc.RootElement;

        if (root.TryGetProperty("choices", out var choices)
            && choices.GetArrayLength() > 0)
        {
            var firstChoice = choices[0];
            if (firstChoice.TryGetProperty("message", out var message)
                && message.TryGetProperty("content", out var messageContent))
            {
                return messageContent.GetString()
                       ?? throw new LlmApiException("openai", "Message content was null");
            }
        }

        throw new LlmApiException("openai", "No choices/message/content found in response");
    }
}
