using System.Text;
using TaskBoard.Worker.Clients;

namespace TaskBoard.Worker.Tests;

public class PromptBuilderTests
{
    private static AgentExecutionContext CreateContext(string? commentsFilePath = null)
        => new(
            TargetCardId: "42",
            TargetCardTitle: "My Task",
            WorkspacePath: "/workspace",
            TaskPrompt: "Do the thing",
            SystemPromptFilePath: "/system.md",
            Model: "test-model",
            CommentsFilePath: commentsFilePath);

    [Fact]
    public void AppendSharedSections_ContainsInstructionsSection()
    {
        var sb = new StringBuilder();
        PromptBuilder.AppendSharedSections(sb, CreateContext(), "/workspace/.aiboard/tasks/42-my-task.md");

        var result = sb.ToString();
        Assert.Contains("## Instructions", result);
        Assert.Contains("/workspace/.aiboard/tasks/42-my-task.md", result);
        Assert.Contains(".aiboard/tasks/ directory", result);
    }

    [Fact]
    public void AppendSharedSections_ContainsQualityGatesSection()
    {
        var sb = new StringBuilder();
        PromptBuilder.AppendSharedSections(sb, CreateContext(), "/tmp/task.md");

        var result = sb.ToString();
        Assert.Contains("## Quality Gates", result);
        Assert.Contains("MUST build successfully", result);
        Assert.Contains("All existing tests MUST pass", result);
        Assert.Contains("New features MUST have test coverage", result);
    }

    [Fact]
    public void AppendSharedSections_ContainsOutcomeRulesSection()
    {
        var sb = new StringBuilder();
        PromptBuilder.AppendSharedSections(sb, CreateContext(), "/tmp/task.md");

        var result = sb.ToString();
        Assert.Contains("## Outcome Rules", result);
        Assert.Contains("COMPLETE", result);
        Assert.Contains("NEEDS_INFO", result);
        Assert.Contains("ERROR", result);
    }

    [Fact]
    public void AppendSharedSections_WithCommentsFile_IncludesPriorConversationSection()
    {
        var sb = new StringBuilder();
        PromptBuilder.AppendSharedSections(sb, CreateContext("/tmp/comments.md"), "/tmp/task.md");

        var result = sb.ToString();
        Assert.Contains("## Prior Conversation", result);
        Assert.Contains("/tmp/comments.md", result);
        Assert.Contains("Reviewer Directives", result);
        Assert.Contains("AUTHORITATIVE", result);
        Assert.Contains("Agent History", result);
        Assert.Contains("advisory, not authoritative", result);
        Assert.Contains("READ-ONLY", result);
    }

    [Fact]
    public void AppendSharedSections_WithoutCommentsFile_NoPriorConversationSection()
    {
        var sb = new StringBuilder();
        PromptBuilder.AppendSharedSections(sb, CreateContext(commentsFilePath: null), "/tmp/task.md");

        var result = sb.ToString();
        Assert.DoesNotContain("## Prior Conversation", result);
        Assert.DoesNotContain("Reviewer Directives", result);
    }

    [Fact]
    public void AppendSharedSections_QualityGatesBeforeOutcomeRules()
    {
        var sb = new StringBuilder();
        PromptBuilder.AppendSharedSections(sb, CreateContext(), "/tmp/task.md");

        var result = sb.ToString();
        var qualityGatesIndex = result.IndexOf("## Quality Gates", StringComparison.Ordinal);
        var outcomeRulesIndex = result.IndexOf("## Outcome Rules", StringComparison.Ordinal);

        Assert.True(qualityGatesIndex >= 0, "Quality Gates section must be present");
        Assert.True(outcomeRulesIndex > qualityGatesIndex, "Outcome Rules must come after Quality Gates");
    }

    [Fact]
    public void AppendSharedSections_InstructionsBeforeQualityGates()
    {
        var sb = new StringBuilder();
        PromptBuilder.AppendSharedSections(sb, CreateContext(), "/tmp/task.md");

        var result = sb.ToString();
        var instructionsIndex = result.IndexOf("## Instructions", StringComparison.Ordinal);
        var qualityGatesIndex = result.IndexOf("## Quality Gates", StringComparison.Ordinal);

        Assert.True(instructionsIndex >= 0, "Instructions section must be present");
        Assert.True(qualityGatesIndex > instructionsIndex, "Quality Gates must come after Instructions");
    }

    [Fact]
    public void AppendSharedSections_AppendsToExistingContent()
    {
        var sb = new StringBuilder();
        sb.AppendLine("existing content");
        PromptBuilder.AppendSharedSections(sb, CreateContext(), "/tmp/task.md");

        var result = sb.ToString();
        Assert.StartsWith("existing content", result);
        Assert.Contains("## Instructions", result);
    }

    [Fact]
    public void AppendSharedSections_ContainsUpdateFileInstructions()
    {
        var sb = new StringBuilder();
        PromptBuilder.AppendSharedSections(sb, CreateContext(), "/tmp/task.md");

        var result = sb.ToString();
        Assert.Contains("## Creating Additional Tickets", result);
        Assert.Contains(".aiboard/updates/", result);
        Assert.Contains("new-{slug}.md", result);
        Assert.Contains("{cardId}-comment.md", result);
    }

    [Fact]
    public void AppendSharedSections_ContainsDeduplicationGuidance()
    {
        var sb = new StringBuilder();
        PromptBuilder.AppendSharedSections(sb, CreateContext(), "/tmp/task.md");

        var result = sb.ToString();
        Assert.Contains("agent-created-ticket:", result);
        Assert.Contains("Deduplication", result);
    }

    [Fact]
    public void AppendSharedSections_UpdateFileInstructionsAfterOutcomeRules()
    {
        var sb = new StringBuilder();
        PromptBuilder.AppendSharedSections(sb, CreateContext(), "/tmp/task.md");

        var result = sb.ToString();
        var outcomeIdx = result.IndexOf("## Outcome Rules", StringComparison.Ordinal);
        var updateIdx = result.IndexOf("## Creating Additional Tickets", StringComparison.Ordinal);

        Assert.True(outcomeIdx >= 0, "Outcome Rules section must be present");
        Assert.True(updateIdx > outcomeIdx, "Update file instructions must come after Outcome Rules");
    }
}
