namespace TaskBoard.Worker.Clients;

public sealed class PgmqOptions
{
    public const string SectionName = "Pgmq";

    public string ConnectionString { get; set; } = "";
    public string PingQueueName { get; set; } = "pings";
    public int VisibilityTimeoutSeconds { get; set; } = 60;
    public int BatchSize { get; set; } = 10;
    public int StaleClaimMinutes { get; set; } = 30;

    /// <summary>
    /// Maximum number of agent runs executing concurrently.
    /// 0 means unlimited (all eligible cards processed in parallel).
    /// </summary>
    public int MaxConcurrentAgents { get; set; } = 0;
}
