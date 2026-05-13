namespace TaskBoard.Worker.Clients;

internal static class DockerPerformanceVolumeManager
{
    private const string DockerExecutable = "docker";
    private const int InitTimeoutSeconds = 120;

    internal static IReadOnlyList<(string VolumeName, string RelativePath)> GetVolumeNames(
        DockerAgentOptionsBase options,
        string worktreePath)
    {
        if (options.PerformanceVolumes is not { Count: > 0 })
            return [];

        return options.PerformanceVolumes
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p =>
            {
                var relPath = DockerMountBuilderBase.NormalizePerformanceVolumePath(p);
                var volumeName = DockerMountBuilderBase.PerformanceVolumeName(worktreePath, relPath);
                return (volumeName, relPath);
            })
            .DistinctBy(v => v.volumeName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static IReadOnlyList<DockerMount> BuildVolumeMounts(
        DockerAgentOptionsBase options,
        DockerMountContext? mountContext)
    {
        var worktreePath = mountContext?.Mounts
            .FirstOrDefault(m => string.Equals(
                m.ContainerPath,
                DockerMountBuilderBase.WorkspaceMountPoint,
                StringComparison.Ordinal))?
            .HostPath;
        if (string.IsNullOrWhiteSpace(worktreePath))
            return [];

        return GetVolumeNames(options, worktreePath)
            .Select(v => new DockerMount
            {
                HostPath = v.VolumeName,
                ContainerPath = $"{DockerMountBuilderBase.WorkspaceMountPoint}/{v.RelativePath}",
                ReadOnly = false,
            })
            .ToArray();
    }

    internal static async Task EnsureAsync(
        DockerAgentOptionsBase options,
        string worktreePath,
        ProcessRunnerDelegate runProcess,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var volumes = GetVolumeNames(options, worktreePath);
        if (volumes.Count == 0)
            return;

        if (!string.IsNullOrWhiteSpace(options.PerformanceVolumeOwner))
            ValidateOwner(options.PerformanceVolumeOwner);

        foreach (var (volumeName, relativePath) in volumes)
        {
            var createArgs = new[]
            {
                "volume", "create",
                "--label", "aiboard-perf=1",
                "--label", $"aiboard-worktree={volumeName}",
                volumeName,
            };

            var (createExit, _, createErr) = await runProcess(
                DockerExecutable, createArgs, worktreePath, InitTimeoutSeconds, cancellationToken,
                agentName: $"Docker performance volume create ({volumeName})");
            if (createExit != 0)
            {
                throw new CliInfrastructureException(
                    $"Failed to create Docker performance volume '{volumeName}' for '{relativePath}' (exit {createExit}): {createErr}");
            }

            if (string.IsNullOrWhiteSpace(options.PerformanceVolumeOwner))
                continue;

            var sentinel = "/init/.aiboard-volume-owner";
            var chownScript =
                $"if [ ! -e {sentinel} ]; then chown -R {options.PerformanceVolumeOwner} /init && touch {sentinel}; fi";
            var chownArgs = new[]
            {
                "run", "--rm",
                "-u", "0",
                "-v", $"{volumeName}:/init",
                options.ImageName,
                "sh", "-c", chownScript,
            };

            var (chownExit, _, chownErr) = await runProcess(
                DockerExecutable, chownArgs, worktreePath, InitTimeoutSeconds, cancellationToken,
                agentName: $"Docker performance volume init ({volumeName})");
            if (chownExit != 0)
            {
                throw new CliInfrastructureException(
                    $"Failed to initialize Docker performance volume '{volumeName}' as '{options.PerformanceVolumeOwner}' (exit {chownExit}): {chownErr}");
            }

            logger.LogDebug(
                "Docker performance volume ready: {VolumeName} -> /workspace/{RelativePath}",
                volumeName, relativePath);
        }
    }

    private static void ValidateOwner(string owner)
    {
        static bool ValidPart(string value) =>
            !string.IsNullOrWhiteSpace(value)
            && value.All(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-' or '.');

        var parts = owner.Split(':');
        if (parts.Length is < 1 or > 2 || parts.Any(p => !ValidPart(p)))
            throw new ArgumentException(
                "PerformanceVolumeOwner may contain only letters, digits, underscores, hyphens, dots, and one optional ':' separator.",
                nameof(owner));
    }
}
