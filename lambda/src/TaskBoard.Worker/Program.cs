using System.Text.Json;
using TaskBoard.Worker.Data;
using TaskBoard.Worker.Handlers;
using TaskBoard.Worker.Processing;
using Npgmq;
using Npgsql;
using NpgsqlTypes;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<NpgsqlDataSource>(_ =>
{
    var configuredConnectionString = Environment.GetEnvironmentVariable("NEON_DATABASE_URL");
    if (string.IsNullOrWhiteSpace(configuredConnectionString))
    {
        throw new InvalidOperationException("NEON_DATABASE_URL is required.");
    }

    var connectionString = NormalizeConnectionString(configuredConnectionString);
    return NpgsqlDataSource.Create(connectionString);
});

builder.Services.AddSingleton<NpgmqClient>(serviceProvider =>
{
    var dataSource = serviceProvider.GetRequiredService<NpgsqlDataSource>();
    return new NpgmqClient(dataSource);
});

builder.Services.AddSingleton<IQueueRepository, QueueRepository>();
builder.Services.AddSingleton<IProcessedEventsRepository, ProcessedEventsRepository>();
builder.Services.Configure<QueueProcessingOptions>(builder.Configuration.GetSection(QueueProcessingOptions.SectionName));
builder.Services.AddSingleton<EventProcessor>();
builder.Services.AddSingleton<DrainHandler>();

var app = builder.Build();

var mode = GetArgument(args, "--mode")?.ToLowerInvariant();
if (mode is "one" or "wait" or "loop")
{
    await RunCliModeAsync(app.Services, mode, args, app.Logger);
    return;
}

app.MapGet("/health", () => Results.Ok(new { ok = true, service = "task-board-lambda-worker" }));

app.MapPost(
    "/drain",
    async (HttpRequest request, DrainHandler drainHandler, CancellationToken cancellationToken) =>
    {
        var expectedSecret = Environment.GetEnvironmentVariable("INTERNAL_KICK_SECRET");
        var providedSecret = request.Headers["x-internal-kick-secret"].ToString();

        if (string.IsNullOrWhiteSpace(expectedSecret) || !string.Equals(expectedSecret, providedSecret, StringComparison.Ordinal))
        {
            return Results.Unauthorized();
        }

        var batchSizeArg = request.Query["batchSize"].ToString();
        var visibilityTimeoutArg = request.Query["visibilityTimeoutSeconds"].ToString();
        int? batchSize = int.TryParse(batchSizeArg, out var parsedBatchSize) ? parsedBatchSize : null;
        var visibilityTimeoutSeconds = int.TryParse(visibilityTimeoutArg, out var parsedVisibilityTimeoutSeconds)
            ? (int?)parsedVisibilityTimeoutSeconds
            : null;

        var result = await drainHandler.HandleAsync(batchSize, visibilityTimeoutSeconds, cancellationToken);
        return Results.Ok(result);
    });

app.Run();

static string? GetArgument(string[] args, string key)
{
    var index = Array.FindIndex(args, value => string.Equals(value, key, StringComparison.OrdinalIgnoreCase));
    if (index < 0 || index + 1 >= args.Length)
    {
        return null;
    }

    return args[index + 1];
}

static async Task RunCliModeAsync(IServiceProvider services, string mode, string[] args, ILogger logger)
{
    using var scope = services.CreateScope();
    var processor = scope.ServiceProvider.GetRequiredService<EventProcessor>();
    var cancellationToken = CancellationToken.None;

    if (mode == "one")
    {
        var result = await processor.ProcessOneAsync(cancellationToken);
        logger.LogInformation("{Result}", JsonSerializer.Serialize(result));
        return;
    }

    if (mode == "wait")
    {
        var waitSeconds = int.TryParse(GetArgument(args, "--wait-seconds"), out var parsedWaitSeconds)
            ? parsedWaitSeconds
            : 30;

        var result = await processor.ProcessOneOrWaitAsync(TimeSpan.FromSeconds(waitSeconds), cancellationToken);
        logger.LogInformation("{Result}", JsonSerializer.Serialize(result));
        return;
    }

    logger.LogInformation("Starting continuous loop mode. Stop with Ctrl+C.");
    await processor.RunLoopAsync(cancellationToken);
}

static string NormalizeConnectionString(string value)
{
    if (!value.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase)
        && !value.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
    {
        return value;
    }

    if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
    {
        throw new InvalidOperationException("NEON_DATABASE_URL is not a valid postgres URI.");
    }

    var builder = new NpgsqlConnectionStringBuilder
    {
        Host = uri.Host,
        Port = uri.IsDefaultPort ? 5432 : uri.Port,
        Database = uri.AbsolutePath.Trim('/'),
        Username = GetUsername(uri),
        Password = GetPassword(uri)
    };

    foreach (var (key, rawValue) in ParseQuery(uri.Query))
    {
        var valueToAssign = Uri.UnescapeDataString(rawValue);

        if (string.Equals(key, "sslmode", StringComparison.OrdinalIgnoreCase)
            && Enum.TryParse<SslMode>(valueToAssign, true, out var sslMode))
        {
            builder.SslMode = sslMode;
            continue;
        }

        if (string.Equals(key, "channel_binding", StringComparison.OrdinalIgnoreCase)
            && Enum.TryParse<ChannelBinding>(valueToAssign, true, out var channelBinding))
        {
            builder.ChannelBinding = channelBinding;
            continue;
        }

        if (string.Equals(key, "application_name", StringComparison.OrdinalIgnoreCase))
        {
            builder.ApplicationName = valueToAssign;
        }
    }

    return builder.ConnectionString;
}

static string GetUsername(Uri uri)
{
    if (string.IsNullOrWhiteSpace(uri.UserInfo))
    {
        return string.Empty;
    }

    var separatorIndex = uri.UserInfo.IndexOf(':');
    var username = separatorIndex >= 0 ? uri.UserInfo[..separatorIndex] : uri.UserInfo;
    return Uri.UnescapeDataString(username);
}

static string GetPassword(Uri uri)
{
    if (string.IsNullOrWhiteSpace(uri.UserInfo))
    {
        return string.Empty;
    }

    var separatorIndex = uri.UserInfo.IndexOf(':');
    if (separatorIndex < 0 || separatorIndex + 1 >= uri.UserInfo.Length)
    {
        return string.Empty;
    }

    return Uri.UnescapeDataString(uri.UserInfo[(separatorIndex + 1)..]);
}

static IEnumerable<(string Key, string Value)> ParseQuery(string query)
{
    if (string.IsNullOrWhiteSpace(query))
    {
        yield break;
    }

    var trimmed = query.TrimStart('?');
    if (string.IsNullOrWhiteSpace(trimmed))
    {
        yield break;
    }

    var pairs = trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries);
    foreach (var pair in pairs)
    {
        var split = pair.Split('=', 2);
        var key = split[0];
        var value = split.Length == 2 ? split[1] : string.Empty;
        if (!string.IsNullOrWhiteSpace(key))
        {
            yield return (Uri.UnescapeDataString(key), value);
        }
    }
}
