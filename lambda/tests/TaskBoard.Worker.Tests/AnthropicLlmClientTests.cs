using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Tests.Helpers;

namespace TaskBoard.Worker.Tests;

public class AnthropicLlmClientTests
{
    private static readonly AnthropicLlmOptions DefaultOptions = new()
    {
        ApiKey = "sk-ant-test-key"
    };

    private static readonly string ValidResponseJson = """
        {
          "id": "msg_123",
          "type": "message",
          "role": "assistant",
          "content": [
            {
              "type": "text",
              "text": "{\"updates\":{\"Requirements\":\"Extracted requirements.\"},\"summaryComment\":\"Agent done.\",\"outcome\":\"COMPLETE\",\"approvalRequired\":false}"
            }
          ],
          "model": "claude-sonnet-4-20250514",
          "stop_reason": "end_turn"
        }
        """;

    private static (AnthropicLlmClient Client, MockHttpMessageHandler Handler) CreateSut(AnthropicLlmOptions? options = null)
    {
        var opts = options ?? DefaultOptions;
        var handler = new MockHttpMessageHandler();
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.anthropic.com") };
        var client = new AnthropicLlmClient(httpClient, Options.Create(opts), NullLogger<AnthropicLlmClient>.Instance);
        return (client, handler);
    }

    [Fact]
    public async Task GetCompletion_ValidResponse_ReturnsAgentResponse()
    {
        var (sut, handler) = CreateSut();
        handler.EnqueueResponse(HttpStatusCode.OK, ValidResponseJson);

        var result = await sut.GetCompletionAsync("claude-sonnet-4-20250514", "You are a BA.", "Analyze this.", CancellationToken.None);

        Assert.Equal("COMPLETE", result.Outcome);
        Assert.Equal("Agent done.", result.SummaryComment);
        Assert.True(result.Updates.ContainsKey("Requirements"));
        Assert.Equal("Extracted requirements.", result.Updates["Requirements"]);
        Assert.False(result.ApprovalRequired);
    }

    [Fact]
    public async Task GetCompletion_ApiError_ThrowsLlmApiException()
    {
        var (sut, handler) = CreateSut();
        handler.EnqueueResponse(HttpStatusCode.TooManyRequests, """{"error":{"message":"rate limited"}}""");

        var ex = await Assert.ThrowsAsync<LlmApiException>(
            () => sut.GetCompletionAsync("claude-sonnet-4-20250514", "sys", "user", CancellationToken.None));

        Assert.Equal("anthropic", ex.Provider);
        Assert.Contains("TooManyRequests", ex.Message);
    }

    [Fact]
    public async Task GetCompletion_InvalidAgentJson_ThrowsLlmApiException()
    {
        var (sut, handler) = CreateSut();
        var badResponse = """
            {
              "content": [{ "type": "text", "text": "This is not valid JSON for AgentResponse" }]
            }
            """;
        handler.EnqueueResponse(HttpStatusCode.OK, badResponse);

        var ex = await Assert.ThrowsAsync<LlmApiException>(
            () => sut.GetCompletionAsync("claude-sonnet-4-20250514", "sys", "user", CancellationToken.None));

        Assert.Contains("parse", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetCompletion_SetsCorrectHeaders()
    {
        var (sut, handler) = CreateSut();
        handler.EnqueueResponse(HttpStatusCode.OK, ValidResponseJson);

        await sut.GetCompletionAsync("claude-sonnet-4-20250514", "sys", "user", CancellationToken.None);

        var request = Assert.Single(handler.SentRequests);
        Assert.Contains("sk-ant-test-key", request.Headers.GetValues("x-api-key"));
        Assert.Contains("2023-06-01", request.Headers.GetValues("anthropic-version"));
    }

    [Fact]
    public async Task GetCompletion_SendsCorrectRequestBody()
    {
        var (sut, handler) = CreateSut();
        handler.EnqueueResponse(HttpStatusCode.OK, ValidResponseJson);

        await sut.GetCompletionAsync("claude-sonnet-4-20250514", "You are a BA.", "Analyze this ticket.", CancellationToken.None);

        var body = handler.RequestBodies[0]!;
        Assert.Contains("claude-sonnet-4-20250514", body);
        Assert.Contains("You are a BA.", body);
        Assert.Contains("Analyze this ticket.", body);
        Assert.Contains("Respond ONLY with valid JSON", body);

        var request = Assert.Single(handler.SentRequests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Contains("/v1/messages", request.RequestUri!.ToString());
    }
}
