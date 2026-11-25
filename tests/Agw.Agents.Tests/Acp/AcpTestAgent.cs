using System.Diagnostics;
using System.Text.Json;

namespace Agw.Agents.Tests.Acp;

internal sealed class AcpTestAgent
{
    private static readonly Lazy<Task<AcpTestAgent>> Installed = new(InstallAsync);
    private static string DirectoryPath => Path.Combine(AppContext.BaseDirectory, "Acp", "TestAgent");
    public string ScriptPath => Path.Combine(DirectoryPath, "acp-test-agent.mjs");

    public static Task<AcpTestAgent> EnsureInstalledAsync() => Installed.Value;

    private static async Task<AcpTestAgent> InstallAsync()
    {
        await RunNpmAsync(["ci", "--no-audit", "--no-fund"]);
        return new();
    }

    public (string ScenarioPath, string LogPath) CreateScenario(object scenario)
    {
        var directory = Directory.CreateDirectory(
            Path.Combine(AppContext.BaseDirectory, "Acp", Guid.NewGuid().ToString())
        );
        var path = Path.Combine(directory.FullName, "scenario.json");
        File.WriteAllText(path, JsonSerializer.Serialize(scenario));
        return (path, Path.Combine(directory.FullName, "log.jsonl"));
    }

    public IReadOnlyList<JsonElement> ReadLog(string logPath) =>
        File.Exists(logPath)
            ? File.ReadLines(logPath).Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToList()
            : [];

    public async Task<string> PackAsync()
    {
        var output = await RunNpmAsync(["pack", "--json"]);
        return Path.Combine(
            DirectoryPath,
            JsonSerializer.Deserialize<JsonElement>(output)[0].GetProperty("filename").GetString()!
        );
    }

    private static async Task<string> RunNpmAsync(string[] args)
    {
        var start = new ProcessStartInfo("npm")
        {
            WorkingDirectory = DirectoryPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (OperatingSystem.IsWindows())
        {
            var node = Environment
                .GetEnvironmentVariable("PATH")!
                .Split(Path.PathSeparator)
                .Select(path => Path.Combine(path, "node.exe"))
                .First(File.Exists);
            start.FileName = node;
            start.ArgumentList.Add(
                Path.Combine(Path.GetDirectoryName(node)!, "node_modules", "npm", "bin", "npm-cli.js")
            );
        }
        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
            throw new IOException($"npm {args[0]} failed: {await stderr}");
        return await stdout;
    }
}
