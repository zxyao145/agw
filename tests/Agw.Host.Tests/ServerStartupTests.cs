using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Agw.Host.Tests;

/// <summary>
/// Starts the real Standalone Host in child processes, so working directory, SelfLog, and exit codes stay per process.
/// 在子进程中启动实际的 Standalone Host，使工作目录、SelfLog 和退出码只作用于该进程。
/// </summary>
public sealed class ServerStartupTests : IDisposable
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(90);

    private readonly string _directory = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".scratch", $"startup-{Guid.CreateVersion7():N}")
    );
    private readonly List<ServerProcess> _processes = [];

    [Fact]
    public async Task Start_RootWorkingDirectory_UsesDataRootLogs()
    {
        if (OperatingSystem.IsWindows())
            return;

        var data = Path.Combine(_directory, "data");
        var server = Start(Path.GetPathRoot(AppContext.BaseDirectory)!, "serve", $"--AgwDataDir={data}");

        await server.WaitUntilReadyAsync(Path.Combine(data, "runtime", "server.json"));
        await server.StopAsync();

        Assert.Equal(0, server.ExitCode);
        Assert.Contains("Agw Host configured successfully", ReadLogs(Path.Combine(data, "logs"), "agw-info-*.log"));
        Assert.Contains(
            "Agw Host configured successfully",
            ReadLogs(Path.Combine(data, "logs"), "application-standalone-*.log")
        );
    }

    [Fact]
    public async Task Start_ServeWithDirectoryArguments_UsesConfiguredDirectories()
    {
        if (OperatingSystem.IsWindows())
            return;

        var data = Path.Combine(_directory, "data");
        var logs = Path.Combine(_directory, "separate logs");
        var descriptor = Path.Combine(data, "runtime", "server.json");
        var server = Start(_directory, "serve", $"--AgwDataDir={data}", $"--AgwLogDir={logs}");

        await server.WaitUntilReadyAsync(descriptor);
        using (var document = JsonDocument.Parse(await File.ReadAllTextAsync(descriptor, CancellationToken)))
        {
            Assert.Equal(server.Id, document.RootElement.GetProperty("pid").GetInt32());
            Assert.Equal(server.Port, document.RootElement.GetProperty("port").GetInt32());
        }
        await server.StopAsync();

        Assert.Equal(0, server.ExitCode);
        Assert.False(File.Exists(descriptor));
        Assert.False(Directory.Exists(Path.Combine(data, "logs")));
        Assert.Contains("Agw Host configured successfully", ReadLogs(logs, "agw-info-*.log"));
        Assert.Contains("Agw Host configured successfully", ReadLogs(logs, "application-standalone-*.log"));
    }

    [Fact]
    public async Task Start_LogDirectoryIsRegularFile_ReportsDirectoriesFailure()
    {
        if (OperatingSystem.IsWindows())
            return;

        var data = Path.Combine(_directory, "数据 & 'data'");
        var logs = Path.Combine(data, "logs");
        Directory.CreateDirectory(data);
        await File.WriteAllTextAsync(logs, "existing file", CancellationToken);
        var server = Start(_directory, "serve", $"--AgwDataDir={data}");

        var failure = await server.WaitForStartupFailureAsync();

        Assert.Equal("Directories", failure.GetProperty("stage").GetString());
        Assert.Equal(logs, failure.GetProperty("path").GetString());
        Assert.Equal(typeof(IOException).FullName, failure.GetProperty("exceptionType").GetString());
        Assert.Contains(logs, failure.GetProperty("message").GetString());
        Assert.Equal("existing file", await File.ReadAllTextAsync(logs, CancellationToken));
        await server.AssertHttpUnavailableAsync();
    }

    [Theory]
    [InlineData(
        "--Serilog:WriteTo:Files:Args:configure:Info:Args:path=../escape-.log",
        "Serilog:WriteTo:Files:Args:configure:Info:Args:path"
    )]
    [InlineData("--Serilog:WriteTo:Additional:Name=MissingSink", "Unable to find a method called MissingSink")]
    public async Task Start_InvalidLoggerConfiguration_ReportsLoggerFailure(string argument, string expected)
    {
        if (OperatingSystem.IsWindows())
            return;

        var data = Path.Combine(_directory, "data");
        var server = Start(_directory, "serve", $"--AgwDataDir={data}", argument);

        var failure = await server.WaitForStartupFailureAsync();

        Assert.Equal("Logger", failure.GetProperty("stage").GetString());
        Assert.Equal(Path.Combine(data, "logs"), failure.GetProperty("path").GetString());
        Assert.Equal("Agw.Shared.Exceptions.AgwException", failure.GetProperty("exceptionType").GetString());
        Assert.Contains(expected, failure.GetProperty("message").GetString());
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(data, "logs")));
        await server.AssertHttpUnavailableAsync();
    }

    [Fact]
    public async Task Start_InvalidDeploymentConfiguration_ReportsDeploymentFailure()
    {
        if (OperatingSystem.IsWindows())
            return;

        var data = Path.Combine(_directory, "data");
        var server = Start(_directory, "serve", $"--AgwDataDir={data}", "--Database:Provider=postgres");

        var failure = await server.WaitForStartupFailureAsync();

        Assert.Equal("Deployment", failure.GetProperty("stage").GetString());
        Assert.Equal(data, failure.GetProperty("path").GetString());
        Assert.Equal("Agw.Shared.Exceptions.AgwException", failure.GetProperty("exceptionType").GetString());
        Assert.Contains("Database:ConnectionString", failure.GetProperty("message").GetString());
        await server.AssertHttpUnavailableAsync();
        foreach (var pattern in new[] { "agw-error-*.log", "agw-info-*.log", "application-standalone-*.log" })
        {
            var fatal = Assert.Single(
                ReadLogs(Path.Combine(data, "logs"), pattern).Split(Environment.NewLine),
                line => line.Contains("[FTL]", StringComparison.Ordinal)
            );
            Assert.Contains("failed to start during", fatal);
            Assert.Contains("Deployment", fatal);
        }
    }

    [Fact]
    public async Task Start_InvalidJsonConfiguration_ReportsConfigurationFailure()
    {
        if (OperatingSystem.IsWindows())
            return;

        var application = CreateApplicationDirectory("""{ "Serilog": """);
        var data = Path.Combine(_directory, "data");
        var server = StartApplication(_directory, application, ["serve", $"--AgwDataDir={data}"]);

        var failure = await server.WaitForStartupFailureAsync();

        Assert.Equal("Configuration", failure.GetProperty("stage").GetString());
        Assert.Equal(JsonValueKind.Null, failure.GetProperty("path").ValueKind);
        Assert.Equal(typeof(InvalidDataException).FullName, failure.GetProperty("exceptionType").GetString());
        Assert.Contains(Path.Combine(application, "appsettings.json"), failure.GetProperty("message").GetString());
        Assert.False(Directory.Exists(data));
        await server.AssertHttpUnavailableAsync();
    }

    public void Dispose()
    {
        foreach (var process in _processes)
            process.Dispose();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    private ServerProcess Start(string workingDirectory, params string[] arguments)
    {
        return StartApplication(workingDirectory, AppContext.BaseDirectory, arguments);
    }

    private ServerProcess StartApplication(string workingDirectory, string applicationDirectory, string[] arguments)
    {
        Directory.CreateDirectory(_directory);
        var server = ServerProcess.Start(workingDirectory, applicationDirectory, arguments);
        _processes.Add(server);
        return server;
    }

    /// <summary>
    /// 复制入口程序集并链接其余文件，使子进程的内容根目录使用测试提供的 appsettings.json。
    /// Copies the entry assembly files and links the rest, so the child content root uses the test appsettings.json.
    /// </summary>
    private string CreateApplicationDirectory(string appsettings)
    {
        var application = Path.Combine(_directory, "application");
        Directory.CreateDirectory(application);
        foreach (var entry in new DirectoryInfo(AppContext.BaseDirectory).EnumerateFileSystemInfos())
        {
            var target = Path.Combine(application, entry.Name);
            if (entry.Name.StartsWith("appsettings", StringComparison.Ordinal))
                continue;
            if (entry.Name.StartsWith("agw-server.", StringComparison.Ordinal))
                File.Copy(entry.FullName, target);
            else if (entry is DirectoryInfo)
                Directory.CreateSymbolicLink(target, entry.FullName);
            else
                File.CreateSymbolicLink(target, entry.FullName);
        }

        File.WriteAllText(Path.Combine(application, "appsettings.json"), appsettings);
        return application;
    }

    private static string ReadLogs(string directory, string pattern)
    {
        var files = Directory.GetFiles(directory, pattern).Order(StringComparer.Ordinal).ToArray();
        Assert.NotEmpty(files);
        return string.Concat(files.Select(File.ReadAllText));
    }

    private sealed class ServerProcess : IDisposable
    {
        private readonly Process _process;
        private readonly StringBuilder _output = new();
        private readonly StringBuilder _error = new();
        private readonly ServiceProvider _services;

        private ServerProcess(Process process, int port)
        {
            _process = process;
            Port = port;
            _services = new ServiceCollection().AddHttpClient().BuildServiceProvider();
        }

        public int Id => _process.Id;

        public int Port { get; }

        public int ExitCode => _process.ExitCode;

        public static ServerProcess Start(string workingDirectory, string applicationDirectory, string[] arguments)
        {
            var port = AllocatePort();
            var startInfo = new ProcessStartInfo(
                DotnetHost(),
                [
                    Path.Combine(applicationDirectory, "agw-server.dll"),
                    .. arguments,
                    "--environment=Production",
                    $"--urls=http://127.0.0.1:{port}",
                ]
            )
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            var process = new Process { StartInfo = startInfo };
            var server = new ServerProcess(process, port);
            process.OutputDataReceived += (_, e) => server.Append(server._output, e.Data);
            process.ErrorDataReceived += (_, e) => server.Append(server._error, e.Data);
            Assert.True(process.Start());
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return server;
        }

        public async Task WaitUntilReadyAsync(string descriptor)
        {
            var deadline = TimeProvider.System.GetUtcNow() + StartupTimeout;
            while (!File.Exists(descriptor))
            {
                Assert.False(
                    _process.HasExited,
                    $"Server exited with {SafeExitCode()}.{Environment.NewLine}{Output()}"
                );
                Assert.True(
                    TimeProvider.System.GetUtcNow() < deadline,
                    $"Server did not start.{Environment.NewLine}{Output()}"
                );
                await Task.Delay(TimeSpan.FromMilliseconds(200), CancellationToken);
            }

            using var client = CreateClient();
            using var response = await client.GetAsync("/api/server-info", CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        public async Task StopAsync()
        {
            using var kill = Process.Start(
                "kill",
                ["-TERM", Id.ToString(System.Globalization.CultureInfo.InvariantCulture)]
            )!;
            await kill.WaitForExitAsync(CancellationToken);
            Assert.Equal(0, kill.ExitCode);
            await WaitForExitAsync();
        }

        public async Task<JsonElement> WaitForStartupFailureAsync()
        {
            await WaitForExitAsync();
            Assert.True(ExitCode == 1, $"Server exited with {ExitCode}.{Environment.NewLine}{Output()}");
            var records = Lines(_error)
                .Where(line => line.StartsWith('{'))
                .Select(line => JsonSerializer.Deserialize<JsonElement>(line))
                .Where(record =>
                    record.TryGetProperty("event", out var name) && name.GetString() == "server-startup-failed"
                )
                .ToArray();
            var failure = Assert.Single(records);
            Assert.Equal(1, failure.GetProperty("exitCode").GetInt32());
            Assert.Equal("Standalone", failure.GetProperty("hostProfile").GetString());
            Assert.Equal(Id, failure.GetProperty("processId").GetInt32());
            Assert.Equal(TimeSpan.Zero, failure.GetProperty("timestamp").GetDateTimeOffset().Offset);
            Assert.False(string.IsNullOrWhiteSpace(failure.GetProperty("message").GetString()));
            return failure;
        }

        public async Task AssertHttpUnavailableAsync()
        {
            using var client = CreateClient();
            await Assert.ThrowsAsync<HttpRequestException>(() =>
                client.GetAsync("/api/server-info", CancellationToken)
            );
        }

        public void Dispose()
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
            _process.WaitForExit();
            _process.Dispose();
            _services.Dispose();
        }

        private async Task WaitForExitAsync()
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
            timeout.CancelAfter(StartupTimeout);
            await _process.WaitForExitAsync(timeout.Token);
        }

        private HttpClient CreateClient()
        {
            var client = _services.GetRequiredService<IHttpClientFactory>().CreateClient();
            client.BaseAddress = new Uri($"http://127.0.0.1:{Port}");
            return client;
        }

        private void Append(StringBuilder target, string? line)
        {
            if (line is null)
                return;
            lock (target)
                target.AppendLine(line);
        }

        private static string[] Lines(StringBuilder source)
        {
            lock (source)
                return source.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        }

        private string Output()
        {
            return $"stdout:{Environment.NewLine}{string.Join(Environment.NewLine, Lines(_output))}{Environment.NewLine}stderr:{Environment.NewLine}{string.Join(Environment.NewLine, Lines(_error))}";
        }

        private string SafeExitCode()
        {
            return _process.HasExited
                ? ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : "running";
        }

        private static int AllocatePort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        /// <summary>
        /// 使用运行当前测试的 dotnet 主程序启动 Host。
        /// Starts the Host with the dotnet muxer that runs the current tests.
        /// </summary>
        private static string DotnetHost()
        {
            return Path.GetFullPath(
                Path.Combine(
                    RuntimeEnvironment.GetRuntimeDirectory(),
                    "..",
                    "..",
                    "..",
                    OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"
                )
            );
        }
    }
}
