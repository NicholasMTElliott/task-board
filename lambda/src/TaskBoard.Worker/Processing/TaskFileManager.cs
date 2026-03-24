using System.Text;
using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Models;

namespace TaskBoard.Worker.Processing;

public sealed class TaskFileManager(ILogger<TaskFileManager> logger)
{
    private const string TasksRelativePath = ".aiboard/tasks";

    public async Task WriteAllTaskFilesAsync(
        string workspacePath,
        IReadOnlyList<BoardCard> cards,
        WorkflowConfig workflowConfig,
        CancellationToken cancellationToken)
    {
        var tasksDir = Path.Combine(workspacePath, TasksRelativePath);
        Directory.CreateDirectory(tasksDir);

        foreach (var card in cards)
        {
            var listName = workflowConfig.States.TryGetValue(card.ColumnId, out var state)
                ? state.Name
                : "Unknown";

            var content = BuildTaskFileContent(card, listName);
            var filePath = GetTaskFilePath(workspacePath, card.Id);

            await File.WriteAllTextAsync(filePath, content, Encoding.UTF8, cancellationToken);
        }

        logger.LogInformation("Wrote {Count} task files to {Path}", cards.Count, tasksDir);
    }

    public async Task<string> ReadTaskFileAsync(
        string workspacePath, string cardId, CancellationToken cancellationToken)
    {
        var filePath = GetTaskFilePath(workspacePath, cardId);
        return await File.ReadAllTextAsync(filePath, Encoding.UTF8, cancellationToken);
    }

    public static string GetTaskFilePath(string workspacePath, string cardId)
        => Path.Combine(workspacePath, TasksRelativePath, $"{cardId}.md");

    internal static string BuildTaskFileContent(BoardCard card, string listName)
    {
        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine($"id: {card.Id}");
        sb.AppendLine($"title: {EscapeYamlValue(card.Title)}");
        sb.AppendLine($"list: {listName}");
        sb.AppendLine($"list_id: {card.ColumnId}");
        sb.AppendLine("---");
        sb.AppendLine();
        sb.Append(card.Body);
        return sb.ToString();
    }

    /// <summary>
    /// Extracts the body content from a task file, stripping the YAML front matter.
    /// </summary>
    public static string ExtractBodyFromTaskFile(string taskFileContent)
    {
        if (!taskFileContent.StartsWith("---"))
            return taskFileContent;

        // Find the closing "---" of the front matter (skip the opening "---")
        var endOfFrontMatter = taskFileContent.IndexOf("---", 3, StringComparison.Ordinal);
        if (endOfFrontMatter < 0)
            return taskFileContent;

        var afterFrontMatter = taskFileContent[(endOfFrontMatter + 3)..];

        // Strip the leading newline(s) between front matter and body
        return afterFrontMatter.TrimStart('\r', '\n');
    }

    internal static string EscapeYamlValue(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "\"\"";

        // Quote if value contains characters that could confuse YAML parsers
        if (value.Contains(':') || value.Contains('#') || value.Contains('"')
            || value.Contains('\'') || value.Contains('\n')
            || value.StartsWith(' ') || value.EndsWith(' '))
        {
            return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        return value;
    }
}
