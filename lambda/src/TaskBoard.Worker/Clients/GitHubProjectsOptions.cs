namespace TaskBoard.Worker.Clients;

public sealed class GitHubProjectsOptions
{
    public const string SectionName = "GitHubProjects";

    /// <summary>GitHub user or organization that owns the project.</summary>
    public string Owner { get; init; } = "";

    /// <summary>Repository in "owner/repo" format for issue operations.</summary>
    public string Repo { get; init; } = "";

    /// <summary>Project number (the number in the project URL, e.g. "1").</summary>
    public string ProjectNumber { get; init; } = "";

    /// <summary>
    /// The name of the Status single-select field in the project.
    /// Defaults to "Status" which is the standard GitHub Projects field name.
    /// </summary>
    public string StatusFieldName { get; init; } = "Status";
}
