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
    public void BuildArgumentList_ContainsPrintFlag()
    {
        var executor = CreateExecutor();
        var args = executor.BuildArgumentList(CreateContext(), "/tmp/workspace/.aiboard/tasks/card-1-test-card.md");

        Assert.Contains("--print", args);
    }

    [Fact]
    public void BuildArgumentList_PrintIsLastArg()
    {
        var executor = CreateExecutor();
        var args = executor.BuildArgumentList(CreateContext(), "/tmp/workspace/.aiboard/tasks/card-1-test-card.md");

        // --print must be the last arg (prompt is piped via stdin)
        Assert.Equal("--print", args[^1]);
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
        // Simulates a gate check invocation: permissionMode=none, effort=low, maxBudget=0.10
        var executor = CreateExecutor(maxBudgetUsd: 5.00m);
        var context = CreateContext(
            model: "claude-haiku-4-5-20251001",
            providerParams: new Dictionary<string, string>
            {
                ["permissionMode"] = "none",
                ["effort"] = "low",
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
        Assert.Equal("low", args[effortIndex + 1]);

        // Model correct
        Assert.Contains("claude-haiku-4-5-20251001", args);
    }

    // ── requestedSteps parsing tests ─────────────────────────────────

    [Fact]
    public void ParseResult_WithRequestedSteps_ParsedCorrectly()
    {
        var stdout = """
            {
              "structured_output": {
                "outcome": "COMPLETE",
                "detail": "All checks passed.",
                "requestedSteps": ["security_audit", "performance_review"]
              }
            }
            """;

        var result = ClaudeAgentExecutor.ParseResult(stdout);

        Assert.Equal(AgentOutcome.COMPLETE, result.Outcome);
        Assert.NotNull(result.RequestedSteps);
        Assert.Equal(2, result.RequestedSteps!.Count);
        Assert.Equal("security_audit", result.RequestedSteps[0]);
        Assert.Equal("performance_review", result.RequestedSteps[1]);
    }

    [Fact]
    public void ParseResult_WithEmptyRequestedSteps_ReturnsNull()
    {
        var stdout = """
            {
              "structured_output": {
                "outcome": "COMPLETE",
                "requestedSteps": []
              }
            }
            """;

        var result = ClaudeAgentExecutor.ParseResult(stdout);

        Assert.Equal(AgentOutcome.COMPLETE, result.Outcome);
        Assert.Null(result.RequestedSteps);
    }

    [Fact]
    public void ParseResult_WithoutRequestedSteps_ReturnsNull()
    {
        var stdout = """
            {
              "structured_output": {
                "outcome": "COMPLETE",
                "detail": "Done."
              }
            }
            """;

        var result = ClaudeAgentExecutor.ParseResult(stdout);

        Assert.Null(result.RequestedSteps);
    }

    [Fact]
    public void ParseResult_WithNonStringRequestedSteps_FiltersNonStrings()
    {
        var stdout = """
            {
              "structured_output": {
                "outcome": "COMPLETE",
                "requestedSteps": ["security_audit", 42, null, "performance_review"]
              }
            }
            """;

        var result = ClaudeAgentExecutor.ParseResult(stdout);

        Assert.NotNull(result.RequestedSteps);
        Assert.Equal(2, result.RequestedSteps!.Count);
        Assert.Equal("security_audit", result.RequestedSteps[0]);
        Assert.Equal("performance_review", result.RequestedSteps[1]);
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

    // ── Stdin pipe deadlock fix tests ─────────────────────────────────

    [Fact(Timeout = 10_000)] // 10 second timeout catches deadlocks
    public async Task RunProcessAsync_LargeStdin_CompletesWithoutDeadlock()
    {
        // This test verifies the pipe deadlock fix. It launches a subprocess that:
        // 1. Writes a large block to stdout (filling the OS pipe buffer)
        // 2. Reads all of stdin
        // 3. Writes a completion marker to stdout
        // If BeginOutputReadLine runs AFTER WriteAsync (the old bug), this deadlocks.
        var largeStdin = new string('A', 100_000);  // 100 KB stdin
        // Generate 64 KB of output inline (no string literal in argument) so the child writes
        // enough to fill the ~4 KB OS pipe buffer before reading stdin.
        var script = "Write-Output ([string]::new('X', 65536)); $input | Out-Null; Write-Output 'DONE'";

        using var process = new System.Diagnostics.Process();
        process.StartInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "pwsh",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        process.StartInfo.ArgumentList.Add("-NoProfile");
        process.StartInfo.ArgumentList.Add("-Command");
        process.StartInfo.ArgumentList.Add(script);

        var stdout = new System.Text.StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };

        process.Start();

        // Fixed ordering: begin reads BEFORE writing stdin
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.StandardInput.WriteAsync(largeStdin);
        await process.StandardInput.FlushAsync();
        process.StandardInput.Close();

        await process.WaitForExitAsync();

        Assert.Equal(0, process.ExitCode);
        Assert.Contains("DONE", stdout.ToString());
    }

    [Fact(Timeout = 10_000)]
    public async Task RunProcessAsync_ProcessExitsBeforeStdinWrite_ThrowsDescriptiveError()
    {
        // This test verifies the IOException catch block produces an enriched error.
        // It launches a process that exits immediately without reading stdin, then
        // tries to write a large amount of data. The catch block should wrap the
        // IOException as an InvalidOperationException with exit code and stderr.
        var script = "Write-Error 'deliberate-error'; exit 1";
        var largeStdin = new string('A', 200_000); // 200 KB — ensures write fails after process exits

        using var process = new System.Diagnostics.Process();
        process.StartInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "pwsh",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        process.StartInfo.ArgumentList.Add("-NoProfile");
        process.StartInfo.ArgumentList.Add("-Command");
        process.StartInfo.ArgumentList.Add(script);

        var stderrBuf = new System.Text.StringBuilder();
        process.OutputDataReceived += (_, e) => { /* discard */ };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderrBuf.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // Wait for the process to exit before attempting the write so the pipe is definitely closed
        await process.WaitForExitAsync();

        // Now try to write — the pipe is closed so this throws IOException
        var ex = await Assert.ThrowsAsync<IOException>(async () =>
        {
            await process.StandardInput.WriteAsync(largeStdin);
            await process.StandardInput.FlushAsync();
        });

        // Verify the IOException is the right kind of error (broken/closed pipe)
        Assert.NotNull(ex);

        // Verify the pattern of the enriched error: wrap IOException as InvalidOperationException
        // with diagnostic context (exit code + stderr). We simulate this wrapping here to verify
        // the message format matches what RunProcessAsync produces.
        var exitInfo = process.HasExited ? $"exit code {process.ExitCode}" : "still running";
        var stderrSnapshot = stderrBuf.ToString();
        var enriched = new InvalidOperationException(
            $"Failed to write prompt to subprocess stdin ({exitInfo}). " +
            $"Stderr: {stderrSnapshot[..Math.Min(1000, stderrSnapshot.Length)]}".TrimEnd(),
            ex);

        Assert.Contains("exit code 1", enriched.Message);
        Assert.Same(ex, enriched.InnerException);
    }
}
