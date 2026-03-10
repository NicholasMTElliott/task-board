namespace TaskBoard.Worker.Data;

public interface IProcessedEventsRepository
{
    Task<bool> TryRegisterAsync(string actionId, CancellationToken cancellationToken);
}
