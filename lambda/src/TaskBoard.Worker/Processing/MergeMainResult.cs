namespace TaskBoard.Worker.Processing;

public enum MergeMainStatus { UpToDate, Merged, Conflicts }

public sealed record MergeMainResult(
    MergeMainStatus Status,
    string DefaultBranch,
    IReadOnlyList<string> ChangedFiles,
    IReadOnlyList<string> ConflictFiles,
    int CommitCount,
    string Summary);
