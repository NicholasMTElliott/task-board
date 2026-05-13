namespace TaskBoard.Worker.Processing;

public enum MergeMainStatus { UpToDate, Merged, Conflicts }

public sealed record MergeMainResult(
    MergeMainStatus Status,
    string DefaultBranch,
    IReadOnlyList<string> ChangedFiles,
    IReadOnlyList<string> ConflictFiles,
    int CommitCount,
    string Summary,
    // Full SHA of origin/{DefaultBranch} at the time the merge was attempted —
    // i.e. the mainline commit that was (or will be, in the --no-commit conflict
    // case) merged into the card branch. This is the new merge-base between the
    // card branch and mainline, so the gate-check diff base advances to it
    // (Approach D). Null only when the rev-parse failed (best-effort).
    string? MergedMainSha = null);
