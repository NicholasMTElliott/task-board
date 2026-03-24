namespace TaskBoard.Worker.Clients;

public sealed class GitHubProjectsOptions
{
    public const string SectionName = "GitHubProjects";

    /// <summary>GitHub user or organization that owns the project.</summary>
    public string Owner { get; init; } = "";

    /// <summary>Repository in "owner/repo" format for issue operations.</summary>
    public string Repo { get; init; } = "";

    /// <summary>HTML comment marker used to identify agent comments for upsert.</summary>
    public string CommentMarker { get; init; } = "<!-- agent-status -->";

    /// <summary>
    /// The name of the Status single-select field in the project.
    /// Defaults to "Status" which is the standard GitHub Projects field name.
    /// </summary>
    public string StatusFieldName { get; init; } = "Status";
}
