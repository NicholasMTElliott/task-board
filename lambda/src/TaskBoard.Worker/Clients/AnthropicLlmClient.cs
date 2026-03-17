using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TaskBoard.Worker.Models;

namespace TaskBoard.Worker.Clients;

public sealed class AnthropicLlmClient(
    HttpClient httpClient,
    IOptions<AnthropicLlmOptions> options,
    ILogger<AnthropicLlmClient> logger) : ILlmClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly HttpClient _httpClient = httpClient;
    private readonly AnthropicLlmOptions _options = options.Value;
    private readonly ILogger<AnthropicLlmClient> _logger = logger;

    public async Task<AgentResponse> GetCompletionAsync(
        string model, string systemPrompt, string userPrompt, CancellationToken cancellationToken)
    {
        var fullSystemPrompt = systemPrompt + AgentResponseSchema.BuildSchemaInstruction();

        var requestBody = new
        {
            model,
            max_tokens = 4096,
            system = fullSystemPrompt,
            messages = new[] { new { role = "user", content = userPrompt } }
        };

        var json = JsonSerializer.Serialize(requestBody, JsonOptions);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages");
        request.Content = content;
        request.Headers.Add("x-api-key", _options.ApiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");

        _logger.LogInformation("Calling Anthropic API model={Model}", model);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new LlmApiException("anthropic",
                $"API returned {response.StatusCode}: {responseBody}");
        }

        var contentText = ExtractContentText(responseBody);

        if (!AgentResponseParser.TryParse(contentText, out var agentResponse, out var error))
        {
            throw new LlmApiException("anthropic", $"Failed to parse agent response: {error}");
        }

        _logger.LogInformation("Anthropic API call complete, outcome={Outcome}", agentResponse!.Outcome);
        return agentResponse;
    }

    internal static string ExtractContentText(string responseJson)
    {
        using var doc = JsonDocument.Parse(responseJson);
        var root = doc.RootElement;

        if (root.TryGetProperty("content", out var contentArray)
            && contentArray.GetArrayLength() > 0)
        {
            var firstBlock = contentArray[0];
            if (firstBlock.TryGetProperty("text", out var text))
            {
                return text.GetString()
                       ?? throw new LlmApiException("anthropic", "Content text was null");
            }
        }

        throw new LlmApiException("anthropic", "No content text found in response");
    }
}
