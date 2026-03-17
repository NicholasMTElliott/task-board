using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Tests.Helpers;

namespace TaskBoard.Worker.Tests;

public class OpenAiLlmClientTests
{
    private static readonly OpenAiLlmOptions DefaultOptions = new()
    {
        ApiKey = "sk-test-key"
    };

    private static readonly string ValidResponseJson = """
        {
          "id": "chatcmpl-123",
          "object": "chat.completion",
          "choices": [
            {
              "index": 0,
              "message": {
                "role": "assistant",
                "content": "{\"updates\":{\"Requirements\":\"Extracted requirements.\"},\"summaryComment\":\"Agent done.\",\"outcome\":\"COMPLETE\",\"approvalRequired\":false}"
              },
              "finish_reason": "stop"
            }
          ],
          "model": "gpt-4.1"
        }
        """;

    private static (OpenAiLlmClient Client, MockHttpMessageHandler Handler) CreateSut(OpenAiLlmOptions? options = null)
    {
        var opts = options ?? DefaultOptions;
        var handler = new MockHttpMessageHandler();
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com") };
        var client = new OpenAiLlmClient(httpClient, Options.Create(opts), NullLogger<OpenAiLlmClient>.Instance);
        return (client, handler);
    }

    [Fact]
    public async Task GetCompletion_ValidResponse_ReturnsAgentResponse()
    {
        var (sut, handler) = CreateSut();
        handler.EnqueueResponse(HttpStatusCode.OK, ValidResponseJson);

        var result = await sut.GetCompletionAsync("gpt-4.1", "You are a BA.", "Analyze this.", CancellationToken.None);

        Assert.Equal("COMPLETE", result.Outcome);
        Assert.Equal("Agent done.", result.SummaryComment);
        Assert.True(result.Updates.ContainsKey("Requirements"));
        Assert.False(result.ApprovalRequired);
    }

    [Fact]
    public async Task GetCompletion_ApiError_ThrowsLlmApiException()
    {
        var (sut, handler) = CreateSut();
        handler.EnqueueResponse(HttpStatusCode.InternalServerError, """{"error":{"message":"server error"}}""");

        var ex = await Assert.ThrowsAsync<LlmApiException>(
            () => sut.GetCompletionAsync("gpt-4.1", "sys", "user", CancellationToken.None));

        Assert.Equal("openai", ex.Provider);
        Assert.Contains("InternalServerError", ex.Message);
    }

    [Fact]
    public async Task GetCompletion_InvalidAgentJson_ThrowsLlmApiException()
    {
        var (sut, handler) = CreateSut();
        var badResponse = """
            {
              "choices": [{ "message": { "content": "not valid agent json" } }]
            }
            """;
        handler.EnqueueResponse(HttpStatusCode.OK, badResponse);

        var ex = await Assert.ThrowsAsync<LlmApiException>(
            () => sut.GetCompletionAsync("gpt-4.1", "sys", "user", CancellationToken.None));

        Assert.Contains("parse", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetCompletion_SetsCorrectHeaders()
    {
        var (sut, handler) = CreateSut();
        handler.EnqueueResponse(HttpStatusCode.OK, ValidResponseJson);

        await sut.GetCompletionAsync("gpt-4.1", "sys", "user", CancellationToken.None);

        var request = Assert.Single(handler.SentRequests);
        Assert.Contains("Bearer sk-test-key", request.Headers.GetValues("Authorization"));
    }

    [Fact]
    public async Task GetCompletion_SendsCorrectRequestBody()
    {
        var (sut, handler) = CreateSut();
        handler.EnqueueResponse(HttpStatusCode.OK, ValidResponseJson);

        await sut.GetCompletionAsync("gpt-4.1", "You are a BA.", "Analyze this ticket.", CancellationToken.None);

        var body = handler.RequestBodies[0]!;
        Assert.Contains("gpt-4.1", body);
        Assert.Contains("You are a BA.", body);
        Assert.Contains("Analyze this ticket.", body);
        Assert.Contains("json_object", body);
        Assert.Contains("Respond ONLY with valid JSON", body);

        var request = Assert.Single(handler.SentRequests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Contains("/v1/chat/completions", request.RequestUri!.ToString());
    }
}
