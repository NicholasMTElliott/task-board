using TaskBoard.Worker.Clients;

namespace TaskBoard.Worker.Tests;

public class ClaudeAgentExecutorTests
{
    private static ClaudeAgentExecutor CreateExecutor(decimal maxBudgetUsd = 2.00m)
    {
        var options = Microsoft.Extensions.Options.Options.Create(
            new ClaudeCliLlmOptions { MaxBudgetUsd = maxBudgetUsd });
        return new ClaudeAgentExecutor(options,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ClaudeAgentExecutor>.Instance);
    }

    private static AgentExecutionContext CreateContext(
        string model = "claude-sonnet-4-6",
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

    [Fact]
    public void BuildArgumentList_UsesSystemPromptFileFlag()
    {
        var executor = CreateExecutor();
        var args = executor.BuildArgumentList(CreateContext(), "/tmp/workspace/.aiboard/tasks/card-1-test-card.md");

        Assert.Contains("--append-system-prompt-file", args);
        Assert.Contains("/tmp/system.md", args);
    }

    [Fact]
    public void BuildArgumentList_DoesNotContainInlineSystemPromptFlag()
    {
        var executor = CreateExecutor();
        var args = executor.BuildArgumentList(CreateContext(), "/tmp/workspace/.aiboard/tasks/card-1-test-card.md");

        // The inline --append-system-prompt flag (without -file suffix) must not be present
        Assert.DoesNotContain("--append-system-prompt",
            args.Where(a => a != "--append-system-prompt-file"));
    }

    [Fact]
    public void BuildArgumentList_ContainsPromptFlag()
    {
        var executor = CreateExecutor();
        var args = executor.BuildArgumentList(CreateContext(), "/tmp/workspace/.aiboard/tasks/card-1-test-card.md");

        Assert.Contains("-p", args);
    }

    [Fact]
    public void BuildArgumentList_PromptIsLastArg()
    {
        var executor = CreateExecutor();
        var args = executor.BuildArgumentList(CreateContext(), "/tmp/workspace/.aiboard/tasks/card-1-test-card.md");

        // -p and prompt must be the last two args (flags before content to avoid Windows cmd.exe issues)
        Assert.Equal("-p", args[^2]);
    }

    [Fact]
    public void BuildArgumentList_ContainsRequiredFlags()
    {
        var executor = CreateExecutor();
        var args = executor.BuildArgumentList(CreateContext(model: "my-model"), "/tmp/workspace/.aiboard/tasks/card-1-test-card.md");

        Assert.Contains("--model", args);
        Assert.Contains("my-model", args);
        Assert.Contains("--output-format", args);
        Assert.Contains("stream-json", args);
        Assert.Contains("--json-schema", args);
        Assert.Contains("--permission-mode", args);
        Assert.Contains("bypassPermissions", args);
        Assert.Contains("--no-session-persistence", args);
        Assert.DoesNotContain("--bare", args);
        Assert.DoesNotContain("--session-id", args);
    }

    [Fact]
    public void BuildArgumentList_WithEffortParam_IncludesEffortFlag()
    {
        var executor = CreateExecutor();
        var context = CreateContext(providerParams: new Dictionary<string, string> { ["effort"] = "max" });
        var args = executor.BuildArgumentList(context, "/tmp/workspace/.aiboard/tasks/card-1-test-card.md");

        var effortIndex = Array.IndexOf(args, "--effort");
        Assert.True(effortIndex >= 0, "Expected --effort flag");
        Assert.Equal("max", args[effortIndex + 1]);
    }

    [Fact]
    public void BuildArgumentList_WithoutProviderParams_NoEffortFlag()
    {
        var executor = CreateExecutor();
        var args = executor.BuildArgumentList(CreateContext(), "/tmp/workspace/.aiboard/tasks/card-1-test-card.md");

        Assert.DoesNotContain("--effort", args);
    }

    [Fact]
    public void ParseResult_StructuredOutput_ReturnsCorrectOutcome()
    {
        var stdout = """
            {
              "result": "some text",
              "structured_output": {
                "outcome": "SUCCESS"
              }
            }
            """;

        var result = ClaudeAgentExecutor.ParseResult(stdout);

        Assert.Equal(AgentOutcome.COMPLETE, result.Outcome);
    }

    [Fact]
    public void ParseResult_StructuredOutput_NeedsInfo()
    {
        var stdout = """
            {
              "result": "I have questions",
              "structured_output": {
                "outcome": "NEEDS_INFO",
                "detail": "Need more info"
              }
            }
            """;

        var result = ClaudeAgentExecutor.ParseResult(stdout);

        Assert.Equal(AgentOutcome.NEEDS_INFO, result.Outcome);
        Assert.Equal("Need more info", result.Detail);
    }

    [Fact]
    public void ParseResult_StructuredOutput_WithQuestions()
    {
        var stdout = """
            {
              "structured_output": {
                "outcome": "NEEDS_INFO",
                "detail": "Spec is too vague",
                "questions": [
                  {
                    "question": "What is the target component?",
                    "recommendations": ["Auth module", "API gateway"]
                  },
                  {
                    "question": "What problem are we solving?"
                  }
                ]
              }
            }
            """;

        var result = ClaudeAgentExecutor.ParseResult(stdout);

        Assert.Equal(AgentOutcome.NEEDS_INFO, result.Outcome);
        Assert.Equal("Spec is too vague", result.Detail);
        Assert.NotNull(result.Questions);
        Assert.Equal(2, result.Questions!.Count);
        Assert.Equal("What is the target component?", result.Questions[0].Question);
        Assert.NotNull(result.Questions[0].Recommendations);
        Assert.Equal(2, result.Questions[0].Recommendations!.Count);
        Assert.Equal("Auth module", result.Questions[0].Recommendations![0]);
        Assert.Equal("What problem are we solving?", result.Questions[1].Question);
        Assert.Null(result.Questions[1].Recommendations);
    }

    [Fact]
    public void ParseResult_StructuredOutput_EmptyQuestions_ReturnsNull()
    {
        var stdout = """
            {
              "structured_output": {
                "outcome": "COMPLETE",
                "questions": []
              }
            }
            """;

        var result = ClaudeAgentExecutor.ParseResult(stdout);

        Assert.Equal(AgentOutcome.COMPLETE, result.Outcome);
        Assert.Null(result.Questions);
    }

    [Fact]
    public void ParseResult_ResultFieldWithJson_ReturnsCorrectOutcome()
    {
        var stdout = """
            {
              "result": "{\"outcome\":\"QUESTIONS\"}",
              "session_id": "sess-123"
            }
            """;

        var result = ClaudeAgentExecutor.ParseResult(stdout);

        Assert.Equal(AgentOutcome.NEEDS_INFO, result.Outcome);
    }

    [Fact]
    public void ParseResult_ResultFieldWithMarkdownFences_ReturnsCorrectOutcome()
    {
        var stdout = """
            {
              "result": "```json\n{\"outcome\":\"ERROR\"}\n```",
              "session_id": "sess-123"
            }
            """;

        var result = ClaudeAgentExecutor.ParseResult(stdout);

        Assert.Equal(AgentOutcome.ERROR, result.Outcome);
    }

    [Fact]
    public void ParseResult_RawText_FallsBackToKeywordMatch()
    {
        var stdout = "The task was completed with SUCCESS.";

        var result = ClaudeAgentExecutor.ParseResult(stdout);

        Assert.Equal(AgentOutcome.COMPLETE, result.Outcome);
    }

    [Fact]
    public void ParseResult_UnrecognizedText_ReturnsError()
    {
        var stdout = "Something went wrong sideways, no outcome keyword present.";

        var result = ClaudeAgentExecutor.ParseResult(stdout);

        Assert.Equal(AgentOutcome.ERROR, result.Outcome);
    }

    [Fact]
    public void ParseResult_CaseInsensitive_Works()
    {
        var stdout = """
            {
              "structured_output": {
                "outcome": "success"
              }
            }
            """;

        var result = ClaudeAgentExecutor.ParseResult(stdout);

        Assert.Equal(AgentOutcome.COMPLETE, result.Outcome);
    }

    [Fact]
    public void ParseResult_EmptyString_ReturnsError()
    {
        var result = ClaudeAgentExecutor.ParseResult("");

        Assert.Equal(AgentOutcome.ERROR, result.Outcome);
    }

    [Fact]
    public void ParseResult_NullOutcomeInStructuredOutput_ReturnsError()
    {
        var stdout = """
            {
              "structured_output": {
                "outcome": null
              }
            }
            """;

        var result = ClaudeAgentExecutor.ParseResult(stdout);

        Assert.Equal(AgentOutcome.ERROR, result.Outcome);
    }

    [Fact]
    public void ParseStreamOutput_ExtractsResultAndConversation()
    {
        var stdout = string.Join("\n",
            """{"type":"system","content":"You are..."}""",
            """{"type":"assistant","message":{"content":[{"type":"text","text":"I will analyze the codebase."}]}}""",
            """{"type":"assistant","message":{"content":[{"type":"text","text":"Based on my analysis, the design looks good."}]}}""",
            """{"type":"result","result":"done","structured_output":{"outcome":"COMPLETE","detail":"All good"}}"""
        );

        var (resultJson, conversationLog) = CreateExecutor().ParseStreamOutput(stdout);

        Assert.NotNull(resultJson);
        Assert.Contains("structured_output", resultJson);
        Assert.Contains("I will analyze the codebase.", conversationLog);
        Assert.Contains("Based on my analysis", conversationLog);
    }

    [Fact]
    public void ParseStreamOutput_NoAssistantMessages_EmptyLog()
    {
        var stdout = """{"type":"result","result":"done","structured_output":{"outcome":"COMPLETE"}}""";

        var (resultJson, conversationLog) = CreateExecutor().ParseStreamOutput(stdout);

        Assert.NotNull(resultJson);
        Assert.Empty(conversationLog);
    }

    [Fact]
    public void ParseStreamOutput_MalformedLines_SkippedGracefully()
    {
        var stdout = string.Join("\n",
            "not json at all",
            """{"type":"assistant","message":{"content":[{"type":"text","text":"Hello"}]}}""",
            "{broken json",
            """{"type":"result","structured_output":{"outcome":"NEEDS_INFO"}}"""
        );

        var (resultJson, conversationLog) = CreateExecutor().ParseStreamOutput(stdout);

        Assert.NotNull(resultJson);
        Assert.Contains("Hello", conversationLog);
    }

    [Fact]
    public void ParseStreamOutput_NoResultLine_ReturnsNull()
    {
        var stdout = """{"type":"assistant","message":{"content":[{"type":"text","text":"Working..."}]}}""";

        var (resultJson, conversationLog) = CreateExecutor().ParseStreamOutput(stdout);

        Assert.Null(resultJson);
        Assert.Contains("Working...", conversationLog);
    }

    [Fact]
    public void ParseStreamOutput_MultipleResults_PrefersOneWithStructuredOutput()
    {
        var stdout = string.Join("\n",
            """{"type":"result","subtype":"success","structured_output":{"outcome":"COMPLETE","detail":"All good"}}""",
            """{"type":"result","subtype":"success","result":"Follow-up text with no structured output"}"""
        );

        var (resultJson, _) = CreateExecutor().ParseStreamOutput(stdout);

        Assert.NotNull(resultJson);
        Assert.Contains("structured_output", resultJson);
        Assert.Contains("COMPLETE", resultJson);
    }

    // ── Gate check argument list tests ─────────────────────────────

    [Fact]
    public void BuildArgumentList_PermissionModeNone_OmitsPermissionFlags()
    {
        var executor = CreateExecutor();
        var context = CreateContext(providerParams: new Dictionary<string, string>
        {
            ["permissionMode"] = "none"
        });
        var args = executor.BuildArgumentList(context, "/tmp/workspace/.aiboard/tasks/card-1-test-card.md");

        Assert.DoesNotContain("--permission-mode", args);
        Assert.DoesNotContain("bypassPermissions", args);
        Assert.DoesNotContain("--allowedTools", args);

        // Other required flags still present
        Assert.Contains("--model", args);
        Assert.Contains("--output-format", args);
        Assert.Contains("--json-schema", args);
        Assert.Contains("--no-session-persistence", args);
    }

    [Fact]
    public void BuildArgumentList_WithoutPermissionModeNone_IncludesPermissionFlags()
    {
        var executor = CreateExecutor();
        var context = CreateContext(providerParams: new Dictionary<string, string>
        {
            ["effort"] = "max"
        });
        var args = executor.BuildArgumentList(context, "/tmp/workspace/.aiboard/tasks/card-1-test-card.md");

        Assert.Contains("--permission-mode", args);
        Assert.Contains("bypassPermissions", args);
        Assert.Contains("--allowedTools", args);
    }

    [Fact]
    public void BuildArgumentList_MaxBudgetOverride_UsesProviderParamValue()
    {
        var executor = CreateExecutor(maxBudgetUsd: 5.00m);
        var context = CreateContext(providerParams: new Dictionary<string, string>
        {
            ["maxBudget"] = "0.10"
        });
        var args = executor.BuildArgumentList(context, "/tmp/workspace/.aiboard/tasks/card-1-test-card.md");

        var budgetIndex = Array.IndexOf(args, "--max-budget-usd");
        Assert.True(budgetIndex >= 0, "Expected --max-budget-usd flag");
        Assert.Equal("0.10", args[budgetIndex + 1]);
    }

    [Fact]
    public void BuildArgumentList_NoMaxBudgetOverride_UsesGlobalDefault()
    {
        var executor = CreateExecutor(maxBudgetUsd: 3.50m);
        var context = CreateContext();
        var args = executor.BuildArgumentList(context, "/tmp/workspace/.aiboard/tasks/card-1-test-card.md");

        var budgetIndex = Array.IndexOf(args, "--max-budget-usd");
        Assert.True(budgetIndex >= 0, "Expected --max-budget-usd flag");
        Assert.Equal("3.50", args[budgetIndex + 1]);
    }

    [Fact]
    public void BuildArgumentList_GateCheckCombined_CorrectFlags()
    {
        // Simulates a gate check invocation: permissionMode=none, effort=min, maxBudget=0.10
        var executor = CreateExecutor(maxBudgetUsd: 5.00m);
        var context = CreateContext(
            model: "claude-haiku-4-5-20251001",
            providerParams: new Dictionary<string, string>
            {
                ["permissionMode"] = "none",
                ["effort"] = "min",
                ["maxBudget"] = "0.10"
            });
        var args = executor.BuildArgumentList(context, "/tmp/workspace/.aiboard/tasks/card-1-test-card.md");

        // Permission flags omitted
        Assert.DoesNotContain("--permission-mode", args);
        Assert.DoesNotContain("bypassPermissions", args);

        // Budget overridden
        var budgetIndex = Array.IndexOf(args, "--max-budget-usd");
        Assert.Equal("0.10", args[budgetIndex + 1]);

        // Effort flag present
        var effortIndex = Array.IndexOf(args, "--effort");
        Assert.True(effortIndex >= 0, "Expected --effort flag");
        Assert.Equal("min", args[effortIndex + 1]);

        // Model correct
        Assert.Contains("claude-haiku-4-5-20251001", args);
    }

    [Fact]
    public void ParseStreamOutput_SingleResultWithStructuredOutput_ReturnsIt()
    {
        var stdout = """{"type":"result","structured_output":{"outcome":"NEEDS_INFO","detail":"Need more info"}}""";

        var (resultJson, _) = CreateExecutor().ParseStreamOutput(stdout);

        Assert.NotNull(resultJson);
        var parsed = ClaudeAgentExecutor.ParseResult(resultJson);
        Assert.Equal(AgentOutcome.NEEDS_INFO, parsed.Outcome);
        Assert.Equal("Need more info", parsed.Detail);
    }
}
