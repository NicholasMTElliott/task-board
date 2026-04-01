using System.Text;

namespace TaskBoard.Worker.Clients;

/// <summary>
/// Shared prompt section builder for agent executors.
/// Ensures quality gates, outcome rules, and context sections stay in sync
/// between <see cref="ClaudeAgentExecutor"/> and <see cref="CodexAgentExecutor"/>.
/// </summary>
internal static class PromptBuilder
{
    /// <summary>
    /// Appends the shared tail sections to <paramref name="sb"/>:
    /// Instructions, Prior Conversation (when present), Quality Gates, and Outcome Rules.
    /// </summary>
    internal static void AppendSharedSections(
        StringBuilder sb, AgentExecutionContext context, string taskFilePath)
    {
        sb.AppendLine("## Instructions");
        sb.AppendLine();
        sb.AppendLine($"- The target task file is at: {taskFilePath}");
        sb.AppendLine("- All project tasks are in the .aiboard/tasks/ directory for context.");

        if (context.CommentsFilePath is not null)
        {
            sb.AppendLine();
            sb.AppendLine("## Prior Conversation");
            sb.AppendLine();
            sb.AppendLine($"There is a conversation history file for this task at: {context.CommentsFilePath}.");
            sb.AppendLine();
            sb.AppendLine("This file has two sections:");
            sb.AppendLine("- **Reviewer Directives** — Comments from the human project operator. "
                + "These are AUTHORITATIVE. If a reviewer directive conflicts with any prior agent "
                + "recommendation or assumption, the reviewer directive takes precedence. "
                + "Always read and address reviewer directives before proceeding with your task.");
            sb.AppendLine("- **Agent History** — Output from prior agent runs (design documents, code "
                + "reviews, test results, gate checks). Use this for context about prior work, "
                + "but treat it as advisory, not authoritative.");
            sb.AppendLine();
            sb.AppendLine("**Read this file before starting work.** It is READ-ONLY — do not modify it.");
            sb.AppendLine();
        }

        sb.AppendLine("## Quality Gates");
        sb.AppendLine();
        sb.AppendLine("These are mandatory requirements. Do NOT return COMPLETE if any gate fails:");
        sb.AppendLine("- The project MUST build successfully.");
        sb.AppendLine("- All existing tests MUST pass.");
        sb.AppendLine("- New features MUST have test coverage that proves the requirements are met.");
        sb.AppendLine("- ALL requirements in the ticket description MUST be addressed — both the literal text and the spirit/intent.");
        sb.AppendLine();
        sb.AppendLine("## Outcome Rules");
        sb.AppendLine();
        sb.AppendLine("- **COMPLETE**: All quality gates pass and the work is fully done. Use this ONLY when there are zero blocking issues.");
        sb.AppendLine("- **NEEDS_INFO**: Any quality gate fails, any requirement is unmet, or you need answers before proceeding. Describe each issue as a question in the questions array with recommendations for resolution.");
        sb.AppendLine("- **ERROR**: Something went wrong that prevents you from doing the work at all (e.g. missing files, broken environment). Describe the issue in the detail field.");
        sb.AppendLine("- Always include a summary in the detail field of your structured response, regardless of outcome. This summary is posted as a comment on the ticket.");
        sb.AppendLine("- Format the detail field as GitHub-flavored markdown. Use headings, tables, bullet points, and code blocks as appropriate. This content is rendered directly on a GitHub issue.");
    }
}
