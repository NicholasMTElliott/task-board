namespace TaskBoard.Worker.Clients;

/// <summary>
/// Configuration for the PostgreSQL database connection.
/// Used by run tracking (IRunStore) and metrics queries (IMetricsStore).
/// </summary>
public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    public string ConnectionString { get; set; } = "";
}
