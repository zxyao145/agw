using System.Collections;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;

namespace CodexSdk;

internal sealed class CodexExecArgs
{
    public required string Input { get; init; }
    public string? BaseUrl { get; init; }
    public string? ApiKey { get; init; }
    public string? ThreadId { get; init; }
    public IReadOnlyList<string>? Images { get; init; }
    public string? Model { get; init; }
    public SandboxMode? SandboxMode { get; init; }
    public string? WorkingDirectory { get; init; }
    public IReadOnlyList<string>? AdditionalDirectories { get; init; }
    public bool? SkipGitRepoCheck { get; init; }
    public string? OutputSchemaFile { get; init; }
    public ModelReasoningEffort? ModelReasoningEffort { get; init; }
    public bool? NetworkAccessEnabled { get; init; }
    public WebSearchMode? WebSearchMode { get; init; }
    public bool? WebSearchEnabled { get; init; }
    public ApprovalMode? ApprovalPolicy { get; init; }
    public CancellationToken CancellationToken { get; init; }
}

internal sealed class CodexExec
{
    private const string InternalOriginatorEnv = "CODEX_INTERNAL_ORIGINATOR_OVERRIDE";
    private const string CSharpSdkOriginator = "codex_sdk_cs";

    private readonly string _executablePath;
    private readonly IReadOnlyDictionary<string, string>? _environment;
    private readonly Dictionary<string, JsonNode?>? _configOverrides;

    public CodexExec(string? executablePath = null, IReadOnlyDictionary<string, string>? environment = null, Dictionary<string, JsonNode?>? configOverrides = null)
    {
        _executablePath = executablePath ?? FindCodexPath();
        _environment = environment;
        _configOverrides = configOverrides;
    }

    public async IAsyncEnumerable<string> RunAsync(CodexExecArgs args)
    {
        var commandArgs = BuildCommandArgs(args);
        using var process = CreateProcess(commandArgs, args);

        process.Start();
        await process.StandardInput.WriteAsync(args.Input);
        process.StandardInput.Close();

        var stderr = new StringBuilder();
        var stderrTask = Task.Run(async () =>
        {
            while (!process.StandardError.EndOfStream)
            {
                var line = await process.StandardError.ReadLineAsync();
                if (line is not null)
                {
                    stderr.AppendLine(line);
                }
            }
        }, CancellationToken.None);

        await foreach (var line in ReadStdOutLinesAsync(process, args.CancellationToken))
        {
            yield return line;
        }

        await process.WaitForExitAsync(args.CancellationToken);
        await stderrTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Codex Exec exited with code {process.ExitCode}: {stderr}");
        }
    }

    private Process CreateProcess(IEnumerable<string> commandArgs, CodexExecArgs args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _executablePath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var arg in commandArgs)
        {
            startInfo.ArgumentList.Add(arg);
        }

        startInfo.Environment.Clear();
        var source = _environment ?? Environment.GetEnvironmentVariables().Cast<DictionaryEntry>().ToDictionary(x => (string)x.Key, x => (string?)x.Value ?? string.Empty);
        foreach (var (key, value) in source)
        {
            startInfo.Environment[key] = value;
        }

        if (!startInfo.Environment.ContainsKey(InternalOriginatorEnv))
        {
            startInfo.Environment[InternalOriginatorEnv] = CSharpSdkOriginator;
        }

        if (!string.IsNullOrWhiteSpace(args.BaseUrl))
        {
            startInfo.Environment["OPENAI_BASE_URL"] = args.BaseUrl;
        }

        if (!string.IsNullOrWhiteSpace(args.ApiKey))
        {
            startInfo.Environment["CODEX_API_KEY"] = args.ApiKey;
        }

        var process = new Process { StartInfo = startInfo };
        args.CancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // ignore
            }
        });

        return process;
    }

    private IEnumerable<string> BuildCommandArgs(CodexExecArgs args)
    {
        var commandArgs = new List<string> { "exec", "--experimental-json" };

        if (_configOverrides is not null)
        {
            foreach (var overrideArg in SerializeConfigOverrides(_configOverrides))
            {
                commandArgs.Add("--config");
                commandArgs.Add(overrideArg);
            }
        }

        if (!string.IsNullOrWhiteSpace(args.Model))
        {
            commandArgs.Add("--model");
            commandArgs.Add(args.Model);
        }

        if (args.SandboxMode is not null)
        {
            commandArgs.Add("--sandbox");
            commandArgs.Add(args.SandboxMode.Value.ToWireValue());
        }

        if (!string.IsNullOrWhiteSpace(args.WorkingDirectory))
        {
            commandArgs.Add("--cd");
            commandArgs.Add(args.WorkingDirectory);
        }

        if (args.AdditionalDirectories is not null)
        {
            foreach (var dir in args.AdditionalDirectories)
            {
                commandArgs.Add("--add-dir");
                commandArgs.Add(dir);
            }
        }

        if (args.SkipGitRepoCheck == true)
        {
            commandArgs.Add("--skip-git-repo-check");
        }

        if (!string.IsNullOrWhiteSpace(args.OutputSchemaFile))
        {
            commandArgs.Add("--output-schema");
            commandArgs.Add(args.OutputSchemaFile);
        }

        if (args.ModelReasoningEffort is not null)
        {
            commandArgs.Add("--config");
            commandArgs.Add($"model_reasoning_effort=\"{args.ModelReasoningEffort.Value.ToWireValue()}\"");
        }

        if (args.NetworkAccessEnabled is not null)
        {
            commandArgs.Add("--config");
            commandArgs.Add($"sandbox_workspace_write.network_access={args.NetworkAccessEnabled.Value.ToString().ToLowerInvariant()}");
        }

        if (args.WebSearchMode is not null)
        {
            commandArgs.Add("--config");
            commandArgs.Add($"web_search=\"{args.WebSearchMode.Value.ToWireValue()}\"");
        }
        else if (args.WebSearchEnabled == true)
        {
            commandArgs.Add("--config");
            commandArgs.Add("web_search=\"live\"");
        }
        else if (args.WebSearchEnabled == false)
        {
            commandArgs.Add("--config");
            commandArgs.Add("web_search=\"disabled\"");
        }

        if (args.ApprovalPolicy is not null)
        {
            commandArgs.Add("--config");
            commandArgs.Add($"approval_policy=\"{args.ApprovalPolicy.Value.ToWireValue()}\"");
        }

        if (!string.IsNullOrWhiteSpace(args.ThreadId))
        {
            commandArgs.Add("resume");
            commandArgs.Add(args.ThreadId);
        }

        if (args.Images is not null)
        {
            foreach (var image in args.Images)
            {
                commandArgs.Add("--image");
                commandArgs.Add(image);
            }
        }

        return commandArgs;
    }

    private static async IAsyncEnumerable<string> ReadStdOutLinesAsync(Process process, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (!process.StandardOutput.EndOfStream)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await process.StandardOutput.ReadLineAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(line))
            {
                yield return line;
            }
        }
    }

    private static IEnumerable<string> SerializeConfigOverrides(Dictionary<string, JsonNode?> overrides)
    {
        var result = new List<string>();
        Flatten(overrides, string.Empty, result);
        return result;
    }

    private static void Flatten(JsonNode? value, string prefix, List<string> result)
    {
        if (value is not JsonObject obj)
        {
            if (string.IsNullOrWhiteSpace(prefix))
            {
                throw new InvalidOperationException("Codex config overrides must be a plain object");
            }

            result.Add($"{prefix}={ToTomlValue(value, prefix)}");
            return;
        }

        if (obj.Count == 0)
        {
            if (!string.IsNullOrWhiteSpace(prefix))
            {
                result.Add($"{prefix}={{}}}");
            }

            return;
        }

        foreach (var (key, child) in obj)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new InvalidOperationException("Codex config override keys must be non-empty strings");
            }

            if (child is null)
            {
                continue;
            }

            var path = string.IsNullOrWhiteSpace(prefix) ? key : $"{prefix}.{key}";
            if (child is JsonObject)
            {
                Flatten(child, path, result);
            }
            else
            {
                result.Add($"{path}={ToTomlValue(child, path)}");
            }
        }
    }

    private static string ToTomlValue(JsonNode? value, string path)
    {
        return value switch
        {
            JsonValue primitive => primitive.GetValue<object>() switch
            {
                string s => $"\"{s.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"",
                bool b => b ? "true" : "false",
                byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal => Convert.ToString(primitive.GetValue<object>(), System.Globalization.CultureInfo.InvariantCulture)!,
                _ => throw new InvalidOperationException($"Unsupported Codex config override value at {path}"),
            },
            JsonArray arr => $"[{string.Join(", ", arr.Select((item, index) => ToTomlValue(item, $"{path}[{index}]")))}]",
            JsonObject obj => $"{{{string.Join(", ", obj.Where(x => x.Value is not null).Select(x => $"{FormatTomlKey(x.Key)} = {ToTomlValue(x.Value, $"{path}.{x.Key}")}"))}}}",
            null => throw new InvalidOperationException($"Codex config override at {path} cannot be null"),
            _ => throw new InvalidOperationException($"Unsupported Codex config override value at {path}"),
        };
    }

    private static string FormatTomlKey(string key)
    {
        return System.Text.RegularExpressions.Regex.IsMatch(key, "^[A-Za-z0-9_-]+$")
            ? key
            : $"\"{key.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
    }

    private static string FindCodexPath()
    {
        var triple = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 when RuntimeInformation.IsOSPlatform(OSPlatform.Linux) => "x86_64-unknown-linux-musl",
            Architecture.Arm64 when RuntimeInformation.IsOSPlatform(OSPlatform.Linux) => "aarch64-unknown-linux-musl",
            Architecture.X64 when RuntimeInformation.IsOSPlatform(OSPlatform.OSX) => "x86_64-apple-darwin",
            Architecture.Arm64 when RuntimeInformation.IsOSPlatform(OSPlatform.OSX) => "aarch64-apple-darwin",
            Architecture.X64 when RuntimeInformation.IsOSPlatform(OSPlatform.Windows) => "x86_64-pc-windows-msvc",
            Architecture.Arm64 when RuntimeInformation.IsOSPlatform(OSPlatform.Windows) => "aarch64-pc-windows-msvc",
            _ => throw new PlatformNotSupportedException($"Unsupported platform: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})"),
        };

        var baseDir = AppContext.BaseDirectory;
        var vendorRoot = Path.Combine(baseDir, "..", "vendor");
        var executable = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "codex.exe" : "codex";
        return Path.GetFullPath(Path.Combine(vendorRoot, triple, "codex", executable));
    }
}
