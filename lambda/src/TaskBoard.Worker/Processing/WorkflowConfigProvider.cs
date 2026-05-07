using System.Text.Json;
using TaskBoard.Worker.Models;
using TaskBoard.Worker.Validation;

namespace TaskBoard.Worker.Processing;

/// <summary>
/// Singleton that owns the live <see cref="WorkflowConfig"/> and keeps it
/// up-to-date by watching the workflow JSON file for changes.
///
/// <para>
/// Hot-reload rules:
/// <list type="bullet">
///   <item>File changed → 500 ms debounce → full validation pipeline →
///         atomic swap of <see cref="Current"/> on success.</item>
///   <item>Validation fails → <see cref="Current"/> stays at the
///         most-recent-good value; error is logged visibly.</item>
///   <item>File deleted → error logged; <see cref="Current"/> unchanged;
///         no reload scheduled (FSW's <c>Created</c> event will fire if
///         the file is re-created).</item>
/// </list>
/// </para>
///
/// <para>
/// Per-phase stability: callers should read <see cref="Current"/> once at
/// the top of each processing phase and use that snapshot throughout the
/// phase. Changes are therefore deferred to the next phase.
/// </para>
/// </summary>
public sealed class WorkflowConfigProvider : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _filePath;
    private readonly string _promptBaseDir;
    private readonly ILogger<WorkflowConfigProvider> _logger;
    private readonly FileSystemWatcher? _watcher;
    private readonly object _timerLock = new();
    private Timer? _debounceTimer;

    // volatile so reads from any thread see the latest swap without a lock
    private volatile WorkflowConfig _current;

    public WorkflowConfigProvider(
        string filePath,
        string promptBaseDir,
        WorkflowConfig initialConfig,
        ILogger<WorkflowConfigProvider> logger)
    {
        _filePath = filePath;
        _promptBaseDir = promptBaseDir;
        _logger = logger;
        _current = initialConfig;

        // Only watch when the file actually exists; callers that provided a
        // valid initialConfig from a path that existed at startup are the
        // common case.
        if (!File.Exists(filePath))
        {
            logger.LogWarning(
                "WorkflowConfigProvider: file does not exist, file-watching disabled. Path={Path}",
                filePath);
            return;
        }

        var dir = Path.GetDirectoryName(filePath)!;
        var file = Path.GetFileName(filePath);

        _watcher = new FileSystemWatcher(dir, file)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            EnableRaisingEvents = true,
        };

        _watcher.Changed += OnFileEvent;
        _watcher.Renamed += OnFileEvent;
        _watcher.Created += OnFileEvent;
        _watcher.Deleted += OnDeleted;

        logger.LogInformation(
            "WorkflowConfigProvider: watching {Path} for changes (debounce=500ms)", filePath);
    }

    /// <summary>Returns the most-recent successfully validated config.</summary>
    public WorkflowConfig Current => _current;

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        ScheduleReload();
    }

    private void OnDeleted(object sender, FileSystemEventArgs e)
    {
        _logger.LogError(
            "WorkflowConfigProvider: workflow file was deleted — continuing with last-good config. Path={Path}",
            _filePath);
        // No reload scheduled. The Created event fires if the file is re-created.
    }

    private void ScheduleReload()
    {
        lock (_timerLock)
        {
            _debounceTimer?.Dispose();
            _debounceTimer = new Timer(_ => ReloadNow(), null, TimeSpan.FromMilliseconds(500), Timeout.InfiniteTimeSpan);
        }
    }

    private void ReloadNow()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                _logger.LogError(
                    "WorkflowConfigProvider: reload triggered but file no longer exists — keeping last-good config. Path={Path}",
                    _filePath);
                return;
            }

            var json = File.ReadAllText(_filePath);
            var config = JsonSerializer.Deserialize<WorkflowConfig>(json, JsonOptions)
                ?? throw new InvalidOperationException("Deserialisation returned null.");

            var errors = WorkflowConfigValidator.Validate(config);
            if (errors.Count > 0)
                throw new InvalidOperationException(
                    $"Validation failed:\n{string.Join("\n", errors)}");

            config = config.Normalised();

            var postErrors = WorkflowConfigValidator.Validate(config);
            if (postErrors.Count > 0)
                throw new InvalidOperationException(
                    $"Post-normalisation validation failed:\n{string.Join("\n", postErrors)}");

            config.ConfigDirectory = _promptBaseDir;

            // Atomic swap — any concurrent reader of _current sees either the
            // old or the new value, never a torn one.
            _current = config;

            _logger.LogWarning(
                "WorkflowConfigProvider: workflow reloaded successfully from {Path}", _filePath);

            foreach (var warning in WorkflowConfigValidator.Audit(config))
                _logger.LogWarning("Workflow audit (reload): {Warning}", warning);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "WorkflowConfigProvider: reload FAILED — keeping last-good config. Path={Path}",
                _filePath);
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        lock (_timerLock)
        {
            _debounceTimer?.Dispose();
            _debounceTimer = null;
        }
    }
}
