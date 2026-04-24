using Microsoft.Extensions.Configuration;
using TaskBoard.Worker.Clients;

namespace TaskBoard.Worker.Tests;

public class CodexAgentExecutorTests
{
    private static CodexAgentExecutor CreateExecutor(bool fullAuto = true, string? sandbox = null)
    {
        var options = Microsoft.Extensions.Options.Options.Create(
            new CodexCliLlmOptions { FullAuto = fullAuto, Sandbox = sandbox });
        return new CodexAgentExecutor(options,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CodexAgentExecutor>.Instance);
    }

    private static AgentExecutionContext CreateContext(
        string model = "codex-mini-latest",
        string systemPromptFilePath = "/tmp/system.md",
        IReadOnlyDictionary<string, string>? providerParams = null)
    {
        return new AgentExecutionContext(
            TargetCardId: "card-1",
            TargetCardTitle: "Test Card",
            WorkspacePath: "/tmp/workspace",
            TaskPrompt: "test prompt",
            SystemPromptFilePath: systemPromptFilePath,
            Model: model,
            ProviderParams: providerParams);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // BuildArgumentList
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void BuildArgumentList_StartsWithExecSubcommand()
    {
        var executor = CreateExecutor();
        var args = executor.BuildArgumentList(CreateContext(), "/tmp/schema.json");

        Assert.Equal("exec", args[0]);
    }

    [Fact]
    public void BuildArgumentList_ContainsModelFlag()
    {
        var executor = CreateExecutor();
        var args = executor.BuildArgumentList(CreateContext(model: "my-model"), "/tmp/schema.json");

        var modelIndex = Array.IndexOf(args, "--model");
        Assert.True(modelIndex >= 0, "Expected --model flag");
        Assert.Equal("my-model", args[modelIndex + 1]);
    }

    [Fact]
    public void BuildArgumentList_ContainsJsonFlag()
    {
        var executor = CreateExecutor();
        var args = executor.BuildArgumentList(CreateContext(), "/tmp/schema.json");

        Assert.Contains("--json", args);
    }

    [Fact]
    public void BuildArgumentList_ContainsOutputSchemaFlag()
    {
        var executor = CreateExecutor();
        var args = executor.BuildArgumentList(CreateContext(), "/tmp/schema.json");

        var idx = Array.IndexOf(args, "--output-schema");
        Assert.True(idx >= 0, "Expected --output-schema flag");
        Assert.Equal("/tmp/schema.json", args[idx + 1]);
    }

    [Fact]
    public void BuildArgumentList_ContainsFullAutoFlag_WhenEnabled()
    {
        var executor = CreateExecutor(fullAuto: true);
        var args = executor.BuildArgumentList(CreateContext(), "/tmp/schema.json");

        Assert.Contains("--full-auto", args);
    }

    [Fact]
    public void BuildArgumentList_OmitsFullAutoFlag_WhenDisabled()
    {
        var executor = CreateExecutor(fullAuto: false);
        var args = executor.BuildArgumentList(CreateContext(), "/tmp/schema.json");

        Assert.DoesNotContain("--full-auto", args);
    }

    [Fact]
    public void BuildArgumentList_FullAutoOverrideViaProviderParams()
    {
        var executor = CreateExecutor(fullAuto: true);
        var context = CreateContext(providerParams: new Dictionary<string, string>
        {
            ["fullAuto"] = "false"
        });
        var args = executor.BuildArgumentList(context, "/tmp/schema.json");

        Assert.DoesNotContain("--full-auto", args);
    }

    [Fact]
    public void BuildArgumentList_SandboxFlag_WhenConfigured()
    {
        var executor = CreateExecutor(sandbox: "danger-full-access");
        var args = executor.BuildArgumentList(CreateContext(), "/tmp/schema.json");

        var idx = Array.IndexOf(args, "--sandbox");
        Assert.True(idx >= 0, "Expected --sandbox flag");
        Assert.Equal("danger-full-access", args[idx + 1]);
    }

    [Fact]
    public void BuildArgumentList_SandboxOverrideViaProviderParams()
    {
        var executor = CreateExecutor();
        var context = CreateContext(providerParams: new Dictionary<string, string>
        {
            ["sandbox"] = "workspace-write"
        });
        var args = executor.BuildArgumentList(context, "/tmp/schema.json");

        var idx = Array.IndexOf(args, "--sandbox");
        Assert.True(idx >= 0);
        Assert.Equal("workspace-write", args[idx + 1]);
    }

    [Fact]
    public void BuildArgumentList_StdinMarkerIsLastArg()
    {
        var executor = CreateExecutor();
        var args = executor.BuildArgumentList(CreateContext(), "/tmp/schema.json");

        Assert.Equal("-", args[^1]);
    }

    [Fact]
    public void BuildArgumentList_DoesNotContainClaudeSpecificFlags()
    {
        var executor = CreateExecutor();
        var args = executor.BuildArgumentList(CreateContext(), "/tmp/schema.json");

        Assert.DoesNotContain("--print", args);
        Assert.DoesNotContain("--output-format", args);
        Assert.DoesNotContain("stream-json", args);
        Assert.DoesNotContain("--json-schema", args);
        Assert.DoesNotContain("--no-session-persistence", args);
        Assert.DoesNotContain("--append-system-prompt-file", args);
        Assert.DoesNotContain("--permission-mode", args);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // ParseResult
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ParseResult_StructuredOutput_Complete()
    {
        var json = """
            {
              "type": "turn.completed",
              "structured_output": {
                "outcome": "COMPLETE",
                "detail": "All done"
              }
            }
            """;

        var result = CodexAgentExecutor.ParseResult(json);

        Assert.Equal(AgentOutcome.COMPLETE, result.Outcome);
        Assert.Equal("All done", result.Detail);
    }

    [Fact]
    public void ParseResult_StructuredOutput_SuccessBackwardCompat()
    {
        var json = """
            {
              "structured_output": {
                "outcome": "SUCCESS"
              }
            }
            """;

        var result = CodexAgentExecutor.ParseResult(json);

        Assert.Equal(AgentOutcome.COMPLETE, result.Outcome);
    }

    [Fact]
    public void ParseResult_StructuredOutput_NeedsInfo()
    {
        var json = """
            {
              "structured_output": {
                "outcome": "NEEDS_INFO",
                "detail": "Need more context"
              }
            }
            """;

        var result = CodexAgentExecutor.ParseResult(json);

        Assert.Equal(AgentOutcome.NEEDS_INFO, result.Outcome);
        Assert.Equal("Need more context", result.Detail);
    }

    [Fact]
    public void ParseResult_StructuredOutput_QuestionsBackwardCompat()
    {
        var json = """
            {
              "structured_output": {
                "outcome": "QUESTIONS"
              }
            }
            """;

        var result = CodexAgentExecutor.ParseResult(json);

        Assert.Equal(AgentOutcome.NEEDS_INFO, result.Outcome);
    }

    [Fact]
    public void ParseResult_StructuredOutput_WithQuestionsAndRecommendations()
    {
        var json = """
            {
              "structured_output": {
                "outcome": "NEEDS_INFO",
                "detail": "Spec is vague",
                "questions": [
                  {
                    "question": "What component?",
                    "recommendations": ["Auth module", "API gateway"]
                  },
                  {
                    "question": "What is the deadline?"
                  }
                ]
              }
            }
            """;

        var result = CodexAgentExecutor.ParseResult(json);

        Assert.Equal(AgentOutcome.NEEDS_INFO, result.Outcome);
        Assert.NotNull(result.Questions);
        Assert.Equal(2, result.Questions!.Count);
        Assert.Equal("What component?", result.Questions[0].Question);
        Assert.Equal(2, result.Questions[0].Recommendations!.Count);
        Assert.Equal("Auth module", result.Questions[0].Recommendations![0]);
        Assert.Null(result.Questions[1].Recommendations);
    }

    [Fact]
    public void ParseResult_StructuredOutput_EmptyQuestions_ReturnsNullQuestions()
    {
        var json = """
            {
              "structured_output": {
                "outcome": "COMPLETE",
                "questions": []
              }
            }
            """;

        var result = CodexAgentExecutor.ParseResult(json);

        Assert.Null(result.Questions);
    }

    [Fact]
    public void ParseResult_StructuredOutput_RequestedSteps()
    {
        var json = """
            {
              "structured_output": {
                "outcome": "COMPLETE",
                "requestedSteps": ["security_review", "performance_review"]
              }
            }
            """;

        var result = CodexAgentExecutor.ParseResult(json);

        Assert.Equal(AgentOutcome.COMPLETE, result.Outcome);
        Assert.NotNull(result.RequestedSteps);
        Assert.Equal(2, result.RequestedSteps!.Count);
        Assert.Equal("security_review", result.RequestedSteps[0]);
    }

    [Fact]
    public void ParseResult_ResultField_ExtractsOutcome()
    {
        var json = """
            {
              "result": "{\"outcome\":\"COMPLETE\"}",
              "session_id": "sess-123"
            }
            """;

        var result = CodexAgentExecutor.ParseResult(json);

        Assert.Equal(AgentOutcome.COMPLETE, result.Outcome);
    }

    [Fact]
    public void ParseResult_OutputField_ExtractsOutcome()
    {
        var json = """
            {
              "type": "turn.completed",
              "output": "COMPLETE: task finished"
            }
            """;

        var result = CodexAgentExecutor.ParseResult(json);

        Assert.Equal(AgentOutcome.COMPLETE, result.Outcome);
    }

    [Fact]
    public void ParseResult_RawText_FallsBackToKeywordMatch_Complete()
    {
        var result = CodexAgentExecutor.ParseResult("The task is COMPLETE.");

        Assert.Equal(AgentOutcome.COMPLETE, result.Outcome);
    }

    [Fact]
    public void ParseResult_RawText_FallsBackToKeywordMatch_NeedsInfo()
    {
        var result = CodexAgentExecutor.ParseResult("NEEDS_INFO: missing context");

        Assert.Equal(AgentOutcome.NEEDS_INFO, result.Outcome);
    }

    [Fact]
    public void ParseResult_UnrecognizedText_ReturnsError()
    {
        var result = CodexAgentExecutor.ParseResult("Something exploded with no known outcome.");

        Assert.Equal(AgentOutcome.ERROR, result.Outcome);
    }

    [Fact]
    public void ParseResult_CaseInsensitive()
    {
        var json = """{"structured_output":{"outcome":"complete"}}""";

        var result = CodexAgentExecutor.ParseResult(json);

        Assert.Equal(AgentOutcome.COMPLETE, result.Outcome);
    }

    [Fact]
    public void ParseResult_NullOutcome_ReturnsError()
    {
        var json = """{"structured_output":{"outcome":null}}""";

        var result = CodexAgentExecutor.ParseResult(json);

        Assert.Equal(AgentOutcome.ERROR, result.Outcome);
    }

    [Fact]
    public void ParseResult_EmptyString_ReturnsError()
    {
        var result = CodexAgentExecutor.ParseResult("");

        Assert.Equal(AgentOutcome.ERROR, result.Outcome);
    }

    [Fact]
    public void ParseResult_MarkdownFencedJson_StripsFenceAndParses()
    {
        // If a model wraps the JSON in markdown code fences
        var text = "```json\n{\"outcome\":\"ERROR\"}\n```";

        var result = CodexAgentExecutor.ParseResult(text);

        Assert.Equal(AgentOutcome.ERROR, result.Outcome);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // ParseStreamOutput
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ParseStreamOutput_TurnCompletedWithStructuredOutput_ReturnsResult()
    {
        var stdout = string.Join("\n",
            """{"type":"thread.started","thread_id":"t-1"}""",
            """{"type":"turn.completed","structured_output":{"outcome":"COMPLETE","detail":"Done"}}"""
        );

        var (resultJson, conversationLog) = CreateExecutor().ParseStreamOutput(stdout);

        Assert.NotNull(resultJson);
        var result = CodexAgentExecutor.ParseResult(resultJson!);
        Assert.Equal(AgentOutcome.COMPLETE, result.Outcome);
        Assert.Equal("Done", result.Detail);
    }

    [Fact]
    public void ParseStreamOutput_ItemCompletedEvent_ExtractsTextForLog()
    {
        var stdout = string.Join("\n",
            """{"type":"item.completed","item":{"type":"output_text","text":"I analyzed the codebase."}}""",
            """{"type":"turn.completed","structured_output":{"outcome":"COMPLETE"}}"""
        );

        var (resultJson, conversationLog) = CreateExecutor().ParseStreamOutput(stdout);

        Assert.NotNull(resultJson);
        Assert.Contains("I analyzed the codebase.", conversationLog);
    }

    [Fact]
    public void ParseStreamOutput_MessageWrapperShape_ExtractsTextForLog()
    {
        var stdout = string.Join("\n",
            """{"type":"assistant","message":{"content":[{"type":"text","text":"Here is my analysis."}]}}""",
            """{"type":"turn.completed","structured_output":{"outcome":"COMPLETE"}}"""
        );

        var (resultJson, conversationLog) = CreateExecutor().ParseStreamOutput(stdout);

        Assert.NotNull(resultJson);
        Assert.Contains("Here is my analysis.", conversationLog);
    }

    [Fact]
    public void ParseStreamOutput_OutputArrayShape_ExtractsTextForLog()
    {
        var stdout = string.Join("\n",
            """{"type":"turn.completed","output":[{"type":"output_text","text":"Task is done."}],"structured_output":{"outcome":"COMPLETE"}}"""
        );

        var (resultJson, conversationLog) = CreateExecutor().ParseStreamOutput(stdout);

        Assert.NotNull(resultJson);
        Assert.Contains("Task is done.", conversationLog);
    }

    [Fact]
    public void ParseStreamOutput_NoStructuredOutput_ReturnsNullResultJson()
    {
        var stdout = string.Join("\n",
            """{"type":"thread.started","thread_id":"t-1"}""",
            """{"type":"item.completed","item":{"type":"output_text","text":"Thinking..."}}"""
        );

        var (resultJson, conversationLog) = CreateExecutor().ParseStreamOutput(stdout);

        Assert.Null(resultJson);
    }

    [Fact]
    public void ParseStreamOutput_MultipleEventsWithStructuredOutput_UsesFirst()
    {
        var stdout = string.Join("\n",
            """{"type":"turn.completed","structured_output":{"outcome":"COMPLETE","detail":"First"}}""",
            """{"type":"turn.completed","structured_output":{"outcome":"ERROR","detail":"Second"}}"""
        );

        var (resultJson, _) = CreateExecutor().ParseStreamOutput(stdout);

        Assert.NotNull(resultJson);
        var result = CodexAgentExecutor.ParseResult(resultJson!);
        Assert.Equal(AgentOutcome.COMPLETE, result.Outcome);
        Assert.Equal("First", result.Detail);
    }

    [Fact]
    public void ParseStreamOutput_MalformedLines_AreSkipped()
    {
        var stdout = string.Join("\n",
            "not json at all",
            """{"type":"turn.completed","structured_output":{"outcome":"COMPLETE"}}""",
            "{ broken"
        );

        var (resultJson, _) = CreateExecutor().ParseStreamOutput(stdout);

        Assert.NotNull(resultJson);
        var result = CodexAgentExecutor.ParseResult(resultJson!);
        Assert.Equal(AgentOutcome.COMPLETE, result.Outcome);
    }

    [Fact]
    public void ParseStreamOutput_EmptyInput_ReturnsNullAndEmptyLog()
    {
        var (resultJson, conversationLog) = CreateExecutor().ParseStreamOutput("");

        Assert.Null(resultJson);
        Assert.Equal("", conversationLog);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // BuildCombinedPromptAsync
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task BuildCombinedPromptAsync_NoSystemPromptFile_ContainsTaskPrompt()
    {
        var context = CreateContext(systemPromptFilePath: "/nonexistent/path.md");
        var prompt = await CodexAgentExecutor.BuildCombinedPromptAsync(context, CancellationToken.None);

        Assert.Contains("test prompt", prompt);
    }

    [Fact]
    public async Task BuildCombinedPromptAsync_WithSystemPromptFile_PrependsSystemContent()
    {
        var tmpFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tmpFile, "You are a senior engineer.");
            var context = CreateContext(systemPromptFilePath: tmpFile);
            var prompt = await CodexAgentExecutor.BuildCombinedPromptAsync(context, CancellationToken.None);

            Assert.Contains("You are a senior engineer.", prompt);
            Assert.Contains("## System Instructions", prompt);
            // System instructions must come BEFORE task content
            Assert.True(prompt.IndexOf("System Instructions", StringComparison.Ordinal)
                < prompt.IndexOf("test prompt", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(tmpFile);
        }
    }

    [Fact]
    public async Task BuildCombinedPromptAsync_ContainsQualityGatesSection()
    {
        var context = CreateContext(systemPromptFilePath: "/nonexistent/path.md");
        var prompt = await CodexAgentExecutor.BuildCombinedPromptAsync(context, CancellationToken.None);

        Assert.Contains("Quality Gates", prompt);
        Assert.Contains("COMPLETE", prompt);
        Assert.Contains("NEEDS_INFO", prompt);
    }

    [Fact]
    public async Task BuildCombinedPromptAsync_WithCommentsFile_IncludesConversationSection()
    {
        var context = new AgentExecutionContext(
            TargetCardId: "card-1",
            TargetCardTitle: "Test Card",
            WorkspacePath: "/tmp/workspace",
            TaskPrompt: "test prompt",
            SystemPromptFilePath: "/nonexistent/path.md",
            Model: "codex-mini-latest",
            CommentsFilePath: "/tmp/comments.md");

        var prompt = await CodexAgentExecutor.BuildCombinedPromptAsync(context, CancellationToken.None);

        Assert.Contains("Prior Conversation", prompt);
        Assert.Contains("/tmp/comments.md", prompt);
    }

    [Fact]
    public async Task BuildCombinedPromptAsync_WithoutCommentsFile_NoPriorConversationSection()
    {
        var context = CreateContext();
        var prompt = await CodexAgentExecutor.BuildCombinedPromptAsync(context, CancellationToken.None);

        Assert.DoesNotContain("Prior Conversation", prompt);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // CodexCliResolver
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void CodexCliResolver_ExplicitOverride_IsReturnedUnchanged()
    {
        var result = CodexCliResolver.Resolve("/usr/local/bin/codex");

        Assert.Equal("/usr/local/bin/codex", result);
    }

    [Fact]
    public void CodexCliResolver_ExplicitOverrideCaseInsensitive_IsReturnedUnchanged()
    {
        // "Codex" (mixed case) is not the default "codex" — treated as explicit override
        var result = CodexCliResolver.Resolve("C:\\tools\\Codex.exe");

        Assert.Equal("C:\\tools\\Codex.exe", result);
    }

    [Fact]
    public void CodexCliResolver_DefaultOnNonWindows_ReturnsCodex()
    {
        // On Linux/Mac the default is returned as-is (no platform-specific probe)
        if (!OperatingSystem.IsWindows())
        {
            var result = CodexCliResolver.Resolve("codex");
            Assert.Equal("codex", result);
        }
        else
        {
            // On Windows: returns "codex" or a resolved candidate — either is acceptable
            var result = CodexCliResolver.Resolve("codex");
            Assert.True(result.Contains("codex", StringComparison.OrdinalIgnoreCase));
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // IsRateLimited — merges CodexDefaultPatterns with operator overrides
    // ──────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("rate limit exceeded")]
    [InlineData("HTTP 429 Too Many Requests")]
    [InlineData("insufficient_quota for current billing period")]
    [InlineData("rate_limit_exceeded")]
    public void IsRateLimited_DefaultPatterns_AreDetected(string stderr)
    {
        var executor = CreateExecutor();
        Assert.True(executor.IsRateLimited(stderr));
    }

    [Theory]
    [InlineData("")]
    [InlineData("generic error")]
    [InlineData("authentication failed")]
    public void IsRateLimited_NonMatchingStderr_ReturnsFalse(string stderr)
    {
        var executor = CreateExecutor();
        Assert.False(executor.IsRateLimited(stderr));
    }

    [Fact]
    public void IsRateLimited_OperatorOverridePattern_IsDetected()
    {
        var options = Microsoft.Extensions.Options.Options.Create(
            new CodexCliLlmOptions
            {
                RateLimitPatterns = { "my-custom-throttle-marker" },
            });
        var executor = new CodexAgentExecutor(options,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CodexAgentExecutor>.Instance);

        Assert.True(executor.IsRateLimited("Error: my-custom-throttle-marker hit"));
    }

    [Fact]
    public void IsRateLimited_OperatorOverride_DoesNotReplaceDefaults()
    {
        var options = Microsoft.Extensions.Options.Options.Create(
            new CodexCliLlmOptions
            {
                RateLimitPatterns = { "custom-marker" },
            });
        var executor = new CodexAgentExecutor(options,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CodexAgentExecutor>.Instance);

        // Defaults still apply alongside overrides
        Assert.True(executor.IsRateLimited("rate limit exceeded"));
        Assert.True(executor.IsRateLimited("custom-marker hit"));
    }

    // ──────────────────────────────────────────────────────────────────────────
    // EnvVarsToRemove option
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void EnvVarsToRemove_Default_IsEmpty()
    {
        var opts = new CodexCliLlmOptions();
        Assert.Empty(opts.EnvVarsToRemove);
    }

    [Fact]
    public void EnvVarsToRemove_BindsFromConfiguration()
    {
        var configValues = new Dictionary<string, string?>
        {
            ["CodexCli:EnvVarsToRemove:0"] = "CLAUDECODE",
            ["CodexCli:EnvVarsToRemove:1"] = "CODEX_RUNNING",
        };
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(configValues)
            .Build();

        var opts = new CodexCliLlmOptions();
        config.GetSection(CodexCliLlmOptions.SectionName).Bind(opts);

        Assert.Equal(2, opts.EnvVarsToRemove.Count);
        Assert.Contains("CLAUDECODE", opts.EnvVarsToRemove);
        Assert.Contains("CODEX_RUNNING", opts.EnvVarsToRemove);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // CodexCliResolver.TryGetVersionAsync
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TryGetVersionAsync_MissingExecutable_ReturnsUnknownMarker()
    {
        // A guaranteed-missing path exercises the catch branch and proves
        // the probe never throws.
        var result = await CodexCliResolver.TryGetVersionAsync(
            "/definitely-does-not-exist/codex-binary-xyz-123");

        Assert.StartsWith("(unknown:", result);
    }

    [Fact]
    public void RateLimitPatterns_BindsFromConfiguration()
    {
        var configValues = new Dictionary<string, string?>
        {
            ["CodexCli:RateLimitPatterns:0"] = "custom-throttle",
            ["CodexCli:RateLimitPatterns:1"] = "backoff-requested",
        };
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(configValues)
            .Build();

        var opts = new CodexCliLlmOptions();
        config.GetSection(CodexCliLlmOptions.SectionName).Bind(opts);

        Assert.Equal(2, opts.RateLimitPatterns.Count);
        Assert.Contains("custom-throttle", opts.RateLimitPatterns);
    }
}
