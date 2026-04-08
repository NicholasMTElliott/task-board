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
        sb.AppendLine();
        sb.AppendLine("## Creating Additional Tickets and Cross-Card Comments");
        sb.AppendLine();
        sb.AppendLine("You can request creation of new tickets or post comments on other tickets by creating files in the `.aiboard/updates/` directory.");
        sb.AppendLine();
        sb.AppendLine("### Creating a new ticket");
        sb.AppendLine();
        sb.AppendLine("Create a file named `new-{slug}.md` in `.aiboard/updates/` where `{slug}` is a short, descriptive, URL-safe identifier:");
        sb.AppendLine();
        sb.AppendLine("```markdown");
        sb.AppendLine("---");
        sb.AppendLine("title: Short descriptive title");
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("Detailed description...");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("Create new tickets for:");
        sb.AppendLine("- Bugs discovered that are unrelated to the current work");
        sb.AppendLine("- Bugs or defects from this work that are not blocking and will not be fixed here");
        sb.AppendLine("- Work that could be part of this ticket but is deferred or blocked");
        sb.AppendLine("- Additional tickets requested by human reviewers in the conversation history");
        sb.AppendLine();
        sb.AppendLine("### Commenting on another ticket");
        sb.AppendLine();
        sb.AppendLine("Create a file named `{cardId}-comment.md` in `.aiboard/updates/` where `{cardId}` is the issue number. The file content is the comment body in markdown.");
        sb.AppendLine();
        sb.AppendLine("### Deduplication");
        sb.AppendLine();
        sb.AppendLine("Before creating a `new-{slug}.md` file, check the conversation history for `agent-created-ticket:{slug}` markers. If a ticket with that slug has already been created, do not create the file again.");
        sb.AppendLine();
        sb.AppendLine("## Generating Images");
        sb.AppendLine();
        sb.AppendLine("To include images in a ticket body, new ticket, or cross-card comment:");
        sb.AppendLine();
        sb.AppendLine("1. Write the image file to `.aiboard/images/output/` in the workspace.");
        sb.AppendLine("2. Reference it using standard markdown: `![alt text](.aiboard/images/output/filename.ext)`");
        sb.AppendLine();
        sb.AppendLine("The harness automatically uploads the image and replaces the local path with a hosted URL before posting to GitHub. Supported formats: PNG, JPG/JPEG, GIF, SVG, WebP, BMP (max 10 MB per file).");
        sb.AppendLine();
        sb.AppendLine("**Other image use cases (no upload needed):**");
        sb.AppendLine("- Copy an image URL from another ticket: include the URL directly in markdown.");
        sb.AppendLine("- Reference an image already in the repository: use its `raw.githubusercontent.com` URL.");
    }
}
