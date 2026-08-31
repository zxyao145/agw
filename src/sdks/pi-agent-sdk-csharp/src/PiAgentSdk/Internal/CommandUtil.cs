namespace PiAgentSdk.Internal;

/// <summary>Resolves the Pi CLI target without starting or probing a child process.</summary>
internal static class CommandUtil
{
    /// <summary>Resolves the Pi CLI path from an explicit override, <c>PATH</c>, or known user install locations.</summary>
    /// <param name="overridePath">An explicit Pi executable or shim path.</param>
    /// <returns>The first matching Pi CLI target.</returns>
    /// <exception cref="InvalidOperationException">No Pi CLI target can be resolved.</exception>
    public static string ResolvePiPath(string? overridePath)
    {
        // An explicit caller value is authoritative. Process startup reports an invalid or missing override later.
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return overridePath;
        }

        // Prefer PATH so the SDK follows the same Pi selection as the current command-line environment.
        var fromPath = FindOnPath("pi");
        if (fromPath != null)
        {
            return fromPath;
        }

        // Fall back to common user-scoped npm and local binary locations before checking the system prefix.
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new[]
        {
            Path.Combine(home, ".local", "bin", "pi"),
            Path.Combine(home, ".npm-global", "bin", "pi"),
            Path.Combine(home, "node_modules", ".bin", "pi"),
            "/usr/local/bin/pi",
        };
        foreach (var candidate in candidates)
        {
            var resolved = ResolveExistingCandidate(candidate);
            if (resolved != null)
            {
                return resolved;
            }
        }

        throw new InvalidOperationException(
            "Pi CLI was not found. Install @earendil-works/pi-coding-agent or set PiPathOverride."
        );
    }

    /// <summary>Searches <c>PATH</c> in directory order for an executable or supported Windows shim.</summary>
    /// <param name="command">The command name without a platform-specific extension.</param>
    /// <returns>The resolved path, or <see langword="null"/> when no candidate exists.</returns>
    private static string? FindOnPath(string command)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        // Preserve PATH order because earlier entries intentionally shadow later installations.
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var resolved = ResolveExistingCandidate(Path.Combine(directory, command));
            if (resolved != null)
            {
                return resolved;
            }
        }

        return null;
    }

    /// <summary>Resolves an exact candidate and, on Windows, its supported executable or script variants.</summary>
    /// <param name="path">The candidate path without an assumed Windows extension.</param>
    /// <returns>The existing target path, or <see langword="null"/> when none exists.</returns>
    private static string? ResolveExistingCandidate(string path)
    {
        if (File.Exists(path))
        {
            return path;
        }

        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        // npm exposes Windows commands through .cmd shims; .exe and legacy .bat targets are also supported.
        foreach (var extension in new[] { ".exe", ".cmd", ".bat" })
        {
            if (File.Exists(path + extension))
            {
                return path + extension;
            }
        }

        return null;
    }
}
