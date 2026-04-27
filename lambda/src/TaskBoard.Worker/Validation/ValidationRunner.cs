using TaskBoard.Worker.Models;

namespace TaskBoard.Worker.Validation;

/// <summary>
/// Read-only validation pass: loads workflow config, runs static checks,
/// filesystem (prompt file) checks, and live board-shape cross-checks.
/// Exits with code 1 if any Error finding is produced, else 0.
/// </summary>
public sealed class ValidationRunner(
    WorkflowConfig config,
    IBoardShapeProbe probe,
    IDockerImageProbe dockerImageProbe,
    string boardProvider,
    string boardId,
    ILogger<ValidationRunner> logger)
{
    public async Task<int> RunAsync(CancellationToken ct)
    {
        var findings = new List<ValidationFinding>();

        // Pass 1: static config checks (reuse existing validator)
        foreach (var msg in WorkflowConfigValidator.Validate(config, validatePolling: true))
            findings.Add(new ValidationFinding(ValidationSeverity.Error, "static", "", msg));

        // Pass 2: prompt files on disk
        findings.AddRange(CheckPromptFiles(config));

        // Pass 3: live board introspection
        BoardShape? shape = null;
        try
        {
            shape = await probe.ProbeAsync(boardId, ct);
        }
        catch (Exception ex)
        {
            findings.Add(new ValidationFinding(
                ValidationSeverity.Warning, "board", "",
                $"Board introspection failed: {ex.Message}. Board-side checks skipped."));
        }

        if (shape is null)
        {
            findings.Add(new ValidationFinding(
                ValidationSeverity.Info, "board", "",
                $"Board introspection unavailable for provider '{boardProvider}'. Skipping board-side checks."));
        }
        else
        {
            findings.AddRange(BoardShapeChecks.Check(config, shape));
        }

        // Pass 4: docker images for every docker-* provider referenced by the
        // workflow (roles AND candidate overrides). Catches the "workflow loads
        // fine but runtime fails on missing image" case (KvA v0.0.15 with
        // docker-opencode candidates and no aiboard-opencode-sandbox built).
        try
        {
            var providers = config.GetAllReferencedProviders();
            var checks = await dockerImageProbe.CheckAsync(providers, ct);
            foreach (var c in checks)
            {
                if (c.ExistsLocally) continue;
                var hint = c.ProviderKey switch
                {
                    "docker-claude-cli"  => "build via scripts/build-sandbox.ps1",
                    "docker-opencode"    => "build via scripts/build-opencode-sandbox.ps1",
                    "docker-claude-qwen" => "build via scripts/build-sandbox.ps1 (reuses the Claude image)",
                    _ => null,
                };
                findings.Add(new ValidationFinding(
                    ValidationSeverity.Warning, "docker-image",
                    c.ProviderKey,
                    $"Docker image '{c.ImageName}' not found locally for provider '{c.ProviderKey}'. " +
                    (string.IsNullOrEmpty(c.ProbeError) ? "" : $"docker stderr: {c.ProbeError}"),
                    hint));
            }
        }
        catch (Exception ex)
        {
            findings.Add(new ValidationFinding(
                ValidationSeverity.Info, "docker-image", "",
                $"Docker image probe failed (Docker may be unavailable): {ex.Message}. Skipped."));
        }

        return Report(findings);
    }

    private int Report(IReadOnlyList<ValidationFinding> findings)
    {
        var errors = findings.Where(f => f.Severity == ValidationSeverity.Error).ToList();
        var warnings = findings.Where(f => f.Severity == ValidationSeverity.Warning).ToList();
        var infos = findings.Where(f => f.Severity == ValidationSeverity.Info).ToList();

        foreach (var f in errors) Emit(f);
        foreach (var f in warnings) Emit(f);
        foreach (var f in infos) Emit(f);

        logger.LogInformation(
            "Validation complete: {Errors} error(s), {Warnings} warning(s), {Infos} info.",
            errors.Count, warnings.Count, infos.Count);

        return errors.Count == 0 ? 0 : 1;
    }

    private void Emit(ValidationFinding f)
    {
        var path = string.IsNullOrEmpty(f.Path) ? "" : $" [{f.Path}]";
        var hint = string.IsNullOrEmpty(f.Hint) ? "" : $" (hint: {f.Hint})";
        var line = $"[{f.Category}]{path} {f.Message}{hint}";

        switch (f.Severity)
        {
            case ValidationSeverity.Error: logger.LogError("{Line}", line); break;
            case ValidationSeverity.Warning: logger.LogWarning("{Line}", line); break;
            default: logger.LogInformation("{Line}", line); break;
        }
    }

    private static List<ValidationFinding> CheckPromptFiles(WorkflowConfig config)
    {
        var findings = new List<ValidationFinding>();
        var baseDir = config.ConfigDirectory ?? Directory.GetCurrentDirectory();
        var checkedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var referencedRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (stateId, state) in config.States)
        {
            if (state.Steps is { Count: > 0 })
            {
                foreach (var step in state.Steps)
                {
                    Check(step.TaskPromptFile, $"states[{stateId}].steps[{step.Name}].taskPromptFile");
                    if (!string.IsNullOrWhiteSpace(step.Role)) referencedRoles.Add(step.Role);
                }
            }

            if (state.GateCheck is { } gc)
            {
                Check(gc.TaskPromptFile, $"states[{stateId}].gateCheck.taskPromptFile");
                if (!string.IsNullOrWhiteSpace(gc.Role)) referencedRoles.Add(gc.Role);
            }

            if (state.OptionalSteps is { Count: > 0 })
            {
                foreach (var opt in state.OptionalSteps)
                {
                    Check(opt.TaskPromptFile, $"states[{stateId}].optionalSteps[{opt.Name}].taskPromptFile");
                    if (!string.IsNullOrWhiteSpace(opt.Role)) referencedRoles.Add(opt.Role);
                }
            }
        }

        if (config.MergeResolution is { Role: { Length: > 0 } mrRole })
            referencedRoles.Add(mrRole);

        foreach (var roleName in referencedRoles)
        {
            if (config.Roles.TryGetValue(roleName, out var role))
                Check(role.SystemPromptFile, $"roles[{roleName}].systemPromptFile");
        }

        return findings;

        void Check(string? rel, string path)
        {
            if (string.IsNullOrWhiteSpace(rel)) return;
            var resolved = Path.GetFullPath(Path.IsPathRooted(rel) ? rel : Path.Combine(baseDir, rel));
            if (!checkedPaths.Add(resolved)) return;
            if (!File.Exists(resolved))
                findings.Add(new ValidationFinding(
                    ValidationSeverity.Error, "prompt", path,
                    $"File not found: '{rel}' (resolved to '{resolved}')."));
        }
    }
}
