using TaskBoard.Worker.Clients;

namespace TaskBoard.Worker.Models;

public static class PrerequisiteValidator
{
    /// <summary>
    /// Probes the local system for known AI provider CLIs.
    /// Returns the set of provider keys that are available.
    /// Safe to call before host build (no DI dependencies).
    /// </summary>
    public static async Task<HashSet<string>> DetectAvailableProvidersAsync(
        CancellationToken cancellationToken = default)
    {
        var available = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var claudeExe = OperatingSystem.IsWindows() ? "claude.cmd" : "claude";
        if (await IsCliAvailableAsync(claudeExe, cancellationToken))
            available.Add("claude-cli");

        var codexExe = OperatingSystem.IsWindows() ? "codex.cmd" : "codex";
        if (await IsCliAvailableAsync(codexExe, cancellationToken))
            available.Add("codex");

        return available;
    }

    /// <summary>
    /// Validates that all runtime prerequisites are met for the configured
    /// providers and board. Returns a list of human-readable error messages
    /// (empty = all checks passed).
    /// </summary>
    public static async Task<List<string>> ValidateAsync(
        WorkflowConfig config,
        string boardProvider,
        HashSet<string> availableProviders,
        string promptBaseDirectory,
        GitHubProjectsOptions? githubOptions = null,
        TrelloClientOptions? trelloOptions = null,
        CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();

        // Git is always required (worktree operations)
        await ValidateCliToolAsync(errors, "git", ["--version"],
            "git is required for worktree operations", cancellationToken);

        // Board-provider-specific checks
        switch (boardProvider)
        {
            case "github":
                await ValidateCliToolAsync(errors, "gh", ["--version"],
                    "gh CLI is required for GitHub Projects provider", cancellationToken);
                await ValidateGhAuthAsync(errors, cancellationToken);
                ValidateGitHubOptions(errors, githubOptions);
                break;
            case "trello":
            case "live":
                ValidateTrelloOptions(errors, trelloOptions);
                break;
            // "stub" and unrecognized → no provider checks
        }

        // Validate at least one AI provider is available
        if (availableProviders.Count == 0)
        {
            errors.Add("No AI agent providers are available. " +
                "At least one of claude-cli (Claude CLI) or codex (Codex CLI) must be installed and on PATH.");
        }

        // Prompt file checks
        ValidatePromptFiles(errors, config, promptBaseDirectory);

        return errors;
    }

    /// <summary>
    /// Validates prompt file existence only. Useful for testing
    /// without requiring CLI tools on PATH.
    /// </summary>
    public static List<string> ValidatePromptFiles(
        WorkflowConfig config, string promptBaseDirectory)
    {
        var errors = new List<string>();
        ValidatePromptFiles(errors, config, promptBaseDirectory);
        return errors;
    }

    // --- Private helpers ---

    private static async Task<bool> IsCliAvailableAsync(
        string fileName, CancellationToken cancellationToken)
    {
        var (success, _) = await TryRunCommandAsync(
            fileName, ["--version"], cancellationToken: cancellationToken);
        return success;
    }

    private static async Task ValidateCliToolAsync(
        List<string> errors, string fileName, string[] args,
        string contextMessage, CancellationToken cancellationToken)
    {
        var (success, output) = await TryRunCommandAsync(fileName, args, cancellationToken: cancellationToken);
        if (!success)
        {
            errors.Add($"{contextMessage}. " +
                $"Could not run '{fileName} {string.Join(' ', args)}': {output}");
        }
    }

    private static async Task ValidateGhAuthAsync(List<string> errors, CancellationToken cancellationToken)
    {
        var (success, output) = await TryRunCommandAsync("gh", ["auth", "status"], cancellationToken: cancellationToken);
        if (!success)
        {
            errors.Add("gh CLI is not authenticated. " +
                $"Run 'gh auth login' with project and repo scopes. Detail: {output}");
        }
    }

    private static void ValidateGitHubOptions(
        List<string> errors, GitHubProjectsOptions? options)
    {
        if (options is null)
        {
            errors.Add("GitHubProjectsOptions not configured. " +
                "Set GitHubProjects__Owner, GitHubProjects__Repo, " +
                "and GitHubProjects__ProjectNumber environment variables.");
            return;
        }

        if (string.IsNullOrWhiteSpace(options.Owner))
            errors.Add("GitHubProjects__Owner is not set.");
        if (string.IsNullOrWhiteSpace(options.Repo))
            errors.Add("GitHubProjects__Repo is not set.");
        if (string.IsNullOrWhiteSpace(options.ProjectNumber))
            errors.Add("GitHubProjects__ProjectNumber is not set.");
    }

    private static void ValidateTrelloOptions(
        List<string> errors, TrelloClientOptions? options)
    {
        if (options is null)
        {
            errors.Add("TrelloClientOptions not configured. " +
                "Set Trello__ApiKey and Trello__ApiToken.");
            return;
        }

        if (string.IsNullOrWhiteSpace(options.ApiKey))
            errors.Add("Trello API key is not configured.");
        if (string.IsNullOrWhiteSpace(options.ApiToken))
            errors.Add("Trello API token is not configured.");
    }

    private static void ValidatePromptFiles(
        List<string> errors, WorkflowConfig config,
        string promptBaseDirectory)
    {
        var checkedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var referencedRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (_, state) in config.States)
        {
            if (!string.Equals(state.GateType, GateTypes.AgentRun, StringComparison.OrdinalIgnoreCase))
                continue;

            // Step task prompt files
            if (state.Steps is { Count: > 0 })
            {
                foreach (var step in state.Steps)
                {
                    if (!string.IsNullOrWhiteSpace(step.TaskPromptFile))
                        CheckFileExists(errors, checkedPaths,
                            step.TaskPromptFile, promptBaseDirectory,
                            $"Step '{step.Name}' taskPromptFile");

                    if (!string.IsNullOrWhiteSpace(step.Role))
                        referencedRoles.Add(step.Role);
                }
            }

            // Gate check prompt files
            if (state.GateCheck is not null)
            {
                if (!string.IsNullOrWhiteSpace(state.GateCheck.TaskPromptFile))
                    CheckFileExists(errors, checkedPaths,
                        state.GateCheck.TaskPromptFile, promptBaseDirectory,
                        $"Gate check for '{state.Name}' taskPromptFile");

                if (!string.IsNullOrWhiteSpace(state.GateCheck.Role))
                    referencedRoles.Add(state.GateCheck.Role);
            }
        }

        // Merge resolution role
        if (config.MergeResolution is not null
            && !string.IsNullOrWhiteSpace(config.MergeResolution.Role))
            referencedRoles.Add(config.MergeResolution.Role);

        // System prompt files for referenced roles only
        foreach (var roleName in referencedRoles)
        {
            if (config.Roles.TryGetValue(roleName, out var role)
                && !string.IsNullOrWhiteSpace(role.SystemPromptFile))
            {
                CheckFileExists(errors, checkedPaths,
                    role.SystemPromptFile, promptBaseDirectory,
                    $"Role '{roleName}' systemPromptFile");
            }
        }
    }

    private static void CheckFileExists(
        List<string> errors, HashSet<string> checkedPaths,
        string filePath, string baseDirectory, string context)
    {
        var resolved = Path.IsPathRooted(filePath)
            ? filePath
            : Path.Combine(baseDirectory, filePath);

        var normalized = Path.GetFullPath(resolved);

        if (!checkedPaths.Add(normalized))
            return; // already checked

        if (!File.Exists(normalized))
            errors.Add($"{context} not found: '{filePath}' " +
                $"(resolved to '{normalized}')");
    }

    private static async Task<(bool Success, string Output)> TryRunCommandAsync(
        string fileName, string[] args, int timeoutSeconds = 5,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var process = new System.Diagnostics.Process();
            process.StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = fileName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            foreach (var arg in args)
                process.StartInfo.ArgumentList.Add(arg);

            process.Start();

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                timeoutCts.Token, cancellationToken);

            try
            {
                await process.WaitForExitAsync(linkedCts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }

                if (cancellationToken.IsCancellationRequested)
                    return (false, "Validation cancelled");

                return (false, $"Command timed out after {timeoutSeconds}s");
            }

            var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);

            return process.ExitCode == 0
                ? (true, stdout.Trim())
                : (false, string.IsNullOrWhiteSpace(stderr) ? stdout.Trim() : stderr.Trim());
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return (false, $"Executable not found: {ex.Message}");
        }
        catch (Exception ex)
        {
            return (false, $"Failed to start: {ex.Message}");
        }
    }
}
