namespace TaskBoard.Worker.Data;

public interface IRunLogRepository
{
    Task WriteAsync(string cardId, string role, string? inputHash,
        string? outputHash, string outcome, CancellationToken cancellationToken);
}
