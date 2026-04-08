using System.Diagnostics;

namespace TaskBoard.Worker.Tests.Helpers;

/// <summary>
/// Shared git subprocess helper for tests. Isolates git from the parent
/// process's worktree context by clearing GIT_DIR, GIT_WORK_TREE, and
/// GIT_CEILING_DIRECTORIES. This prevents "Win32 error 5" failures when
/// tests run inside a git worktree on Windows.
/// </summary>
internal static class TestGitHelper
{
    /// <summary>Run a git command synchronously, throwing on non-zero exit.</summary>
    internal static void RunGitSync(string workingDirectory, params string[] args)
    {
        var psi = CreateStartInfo(workingDirectory, args);

        using var process = Process.Start(psi)!;
        process.WaitForExit(30_000);
        if (process.ExitCode != 0)
        {
            var stderr = process.StandardError.ReadToEnd();
            throw new InvalidOperationException(
                $"git {string.Join(' ', args)} failed (exit {process.ExitCode}): {stderr}");
        }
    }

    /// <summary>Run a git command synchronously and return stdout.</summary>
    internal static string RunGitSyncWithOutput(string workingDirectory, params string[] args)
    {
        var psi = CreateStartInfo(workingDirectory, args);

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit(30_000);
        if (process.ExitCode != 0)
        {
            var stderr = process.StandardError.ReadToEnd();
            throw new InvalidOperationException(
                $"git {string.Join(' ', args)} failed (exit {process.ExitCode}): {stderr}");
        }
        return stdout.Trim();
    }

    /// <summary>Initialize a bare git repo suitable for worktree tests.</summary>
    internal static void InitGitRepo(string path)
    {
        RunGitSync(path, "init");
        RunGitSync(path, "config", "user.email", "test@test.com");
        RunGitSync(path, "config", "user.name", "Test");
        File.WriteAllText(Path.Combine(path, ".gitignore"), ".aiboard/\n");
        File.WriteAllText(Path.Combine(path, ".gitkeep"), "");
        RunGitSync(path, "add", ".");
        RunGitSync(path, "commit", "-m", "initial");
    }

    private static ProcessStartInfo CreateStartInfo(string workingDirectory, string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // Isolate from parent worktree context to prevent Windows permission errors
        psi.Environment.Remove("GIT_DIR");
        psi.Environment.Remove("GIT_WORK_TREE");
        psi.Environment.Remove("GIT_CEILING_DIRECTORIES");

        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        return psi;
    }
}
