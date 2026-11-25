using System.Diagnostics;
using System.Text.Json;
using Agw.Agents.Definitions.Domain.Rules;
using Agw.Shared.Exceptions;
using PiAgentSdk.Internal;

namespace Agw.Agents.Infrastructure.Acp;

internal static class AcpPackageInstaller
{
    internal static async Task<string> InstallNpmAsync(
        string stage,
        CommandInfo command,
        string package,
        CancellationToken ct
    )
    {
        var npm = command.Path!;
        var environment = CommandEnvironment(command);
        var arguments = new List<string> { "install", "--prefix", stage, "--no-audit", "--no-fund", "--", package };
        if (OperatingSystem.IsWindows())
        {
            var npmCli = Path.Combine(Path.GetDirectoryName(npm)!, "node_modules", "npm", "bin", "npm-cli.js");
            if (!File.Exists(npmCli))
                throw new AgwException(ErrorCodes.AcpInstallFailed, "Could not locate npm-cli.js beside npm.cmd.");
            arguments.Insert(0, npmCli);
            npm =
                (await CommandUtil.FindAsync("node", cancellationToken: ct)).Path
                ?? throw new AgwException(ErrorCodes.AcpInstallFailed, "Node.js was not found.");
        }
        await RunAsync(npm, arguments, stage, environment, ct);
        using var root = await ReadJsonAsync(Path.Combine(stage, "package.json"), ct);
        if (
            !root.RootElement.TryGetProperty("dependencies", out var deps)
            || deps.ValueKind != JsonValueKind.Object
            || deps.EnumerateObject().Count() != 1
        )
            throw new AgwException(ErrorCodes.AcpInstallFailed, "Expected one installed npm dependency.");
        var name = AcpRegistryRules.NormalizeCmd(deps.EnumerateObject().Single().Name);
        using var packageJson = await ReadJsonAsync(Path.Combine(stage, "node_modules", name, "package.json"), ct);
        if (!packageJson.RootElement.TryGetProperty("bin", out var bin))
            throw new AgwException(ErrorCodes.AcpInstallFailed, $"No bin entry in npm package '{name}'.");
        if (bin.ValueKind == JsonValueKind.Object)
        {
            var entries = bin.EnumerateObject().ToArray();
            bin =
                entries.Length == 1
                    ? entries[0].Value
                    : entries.FirstOrDefault(item => item.Name == name.Split('/')[^1]).Value;
        }
        if (bin.ValueKind != JsonValueKind.String)
            throw new AgwException(
                ErrorCodes.AcpInstallFailed,
                $"Could not select a bin entry in npm package '{name}'."
            );
        return "node_modules/" + name + "/" + AcpRegistryRules.NormalizeCmd(bin.GetString()!);
    }

    private static async Task<JsonDocument> ReadJsonAsync(string path, CancellationToken ct)
    {
        await using var file = File.OpenRead(path);
        return await JsonDocument.ParseAsync(file, cancellationToken: ct);
    }

    internal static async Task<(string Executable, string[] Arguments)> InstallUvAsync(
        string stage,
        CommandInfo command,
        string package,
        CancellationToken ct
    )
    {
        var (name, requirement) = GetPythonRequirement(package);
        var environment = CommandEnvironment(command);
        environment["UV_PYTHON_INSTALL_DIR"] = Path.Combine(stage, "uv-python");
        var python = await InstallPythonAsync(command.Path!, stage, environment, ct);
        await RunAsync(
            command.Path!,
            ["pip", "install", "--break-system-packages", "--python", python, "--", requirement],
            stage,
            environment,
            ct
        );
        return await ResolvePythonEntryAsync(stage, python, name, ct);
    }

    private static (string Name, string Requirement) GetPythonRequirement(string package)
    {
        package = package.Trim();
        var end = package.IndexOfAny(['[', '@', '=', ' ', '<', '>', '!', '~', ';']);
        var name = end < 0 ? package : package[..end];
        if (string.IsNullOrEmpty(name) || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '_')))
            throw new AgwException(ErrorCodes.InvalidParam, "Invalid Python tool package name.");
        var at = package.IndexOf('@');
        var requirement =
            at >= 0 && Version.TryParse(package[(at + 1)..], out _)
                ? package[..at] + "==" + package[(at + 1)..]
                : package;
        return (name, requirement);
    }

    private static async Task<string> InstallPythonAsync(
        string uv,
        string stage,
        IReadOnlyDictionary<string, string> environment,
        CancellationToken ct
    )
    {
        await RunAsync(uv, ["python", "install", "--no-bin"], stage, environment, ct);
        var python = (
            await RunAsync(
                uv,
                ["python", "find", "--managed-python", "--system", "--no-project", "--resolve-links"],
                stage,
                environment,
                ct
            )
        ).Trim();
        AcpRegistryRules.NormalizeCmd(Path.GetRelativePath(stage, python));
        return python;
    }

    private static async Task<(string Executable, string[] Arguments)> ResolvePythonEntryAsync(
        string stage,
        string python,
        string name,
        CancellationToken ct
    )
    {
        using var result = JsonDocument.Parse(
            await RunAsync(python, ["-I", "-c", InspectPythonCommand, name], stage, null, ct)
        );
        var command = result.RootElement.GetProperty("path").GetString()!;
        var interpreter = result.RootElement.GetProperty("interpreter").GetString();
        if (interpreter is null)
            return (AcpRegistryRules.NormalizeCmd(Path.GetRelativePath(stage, command)), []);
        var executable = AcpRegistryRules.NormalizeCmd(Path.GetRelativePath(stage, interpreter));
        return result.RootElement.GetProperty("console").GetBoolean()
            ? (executable, ["-P", "-c", LaunchConsoleEntry, name])
            : (executable, ["-P", "-c", LaunchPythonScript, Path.GetFileName(command)]);
    }

    private const string EntryLookup =
        """
            from importlib.metadata import distribution

            def find_entry(name):
                return next(
                    (entry for entry in distribution(name).entry_points
                     if entry.name == name and entry.group in ("console_scripts", "gui_scripts")),
                    None,
                )
            """ + "\n\n";

    // uv 在长路径上使用 shell 启动 Python 脚本。
    // uv uses a shell trampoline for Python scripts on long paths.
    private const string InspectPythonCommand =
        EntryLookup
        + """
            import json, os, pathlib, shlex, sys, sysconfig

            def script_interpreter(command):
                python = pathlib.Path(sys.executable)
                gui = python.with_name("pythonw.exe")
                if os.name == "nt" and command.suffix.lower() in (".py", ".pyw"):
                    return str(gui if command.suffix.lower() == ".pyw" and gui.is_file() else python)
                max_launcher_line = 256 * 1024
                with command.open("rb") as script:
                    if script.read(2) != b"#!":
                        return None
                    interpreter = os.fsdecode(script.readline(max_launcher_line).rstrip(b"\r\n"))
                    if interpreter == "/bin/sh":
                        line = script.readline(max_launcher_line)
                        if not line.startswith(b"'''exec' "):
                            return None
                        if script.readline(max_launcher_line).rstrip(b"\r\n") != b"' '''":
                            return None
                        try:
                            args = shlex.split(os.fsdecode(line))
                        except ValueError:
                            return None
                        match args:
                            case ["exec", executable, "$0", "$@"]:
                                interpreter = executable
                            case _:
                                return None
                candidates = [python, gui] if os.name == "nt" else [python]
                return next((str(p) for p in candidates if pathlib.Path(interpreter) == p and p.is_file()), None)

            name = sys.argv[1]
            scripts = pathlib.Path(sysconfig.get_path("scripts"))
            filenames = [name + ".exe", name, name + ".py", name + ".pyw"] if os.name == "nt" else [name]
            command = next((scripts / file for file in filenames if (scripts / file).is_file()), None)
            if command is None:
                sys.exit(f"Missing {name}. Installed scripts: {', '.join(p.name for p in scripts.iterdir())}")
            console = find_entry(name) is not None
            interpreter = sys.executable if console else script_interpreter(command)
            print(json.dumps({"path": str(command), "interpreter": interpreter, "console": console}))
            """;

    private const string LaunchConsoleEntry =
        EntryLookup
        + """
            import sys
            name = sys.argv.pop(1)
            sys.argv[0] = name
            sys.exit(find_entry(name).load()())
            """;

    private const string LaunchPythonScript = """
        import pathlib, runpy, sys, sysconfig
        scripts = pathlib.Path(sysconfig.get_path("scripts"))
        sys.path.insert(0, str(scripts))
        runpy.run_path(str(scripts / sys.argv.pop(1)), run_name="__main__")
        """;

    private static Dictionary<string, string> CommandEnvironment(CommandInfo command) =>
        command.SearchPath is { } path ? new() { ["PATH"] = path } : new();

    private static async Task<string> RunAsync(
        string command,
        IEnumerable<string> args,
        string directory,
        IReadOnlyDictionary<string, string>? environment,
        CancellationToken ct
    )
    {
        var start = new ProcessStartInfo(command)
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        if (environment is not null)
            foreach (var (key, value) in environment)
                start.Environment[key] = value;
        using var process =
            Process.Start(start)
            ?? throw new AgwException(ErrorCodes.AcpInstallFailed, $"Could not start '{command}'.");
        var stderr = process.StandardError.ReadToEndAsync(ct);
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        try
        {
            await Task.WhenAll(process.WaitForExitAsync(ct), stderr, stdout);
            if (process.ExitCode != 0)
                throw new AgwException(
                    ErrorCodes.AcpInstallFailed,
                    $"{Path.GetFileName(command)} exited with {process.ExitCode}: {await stderr}"
                );
            return await stdout;
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
    }
}
