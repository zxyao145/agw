using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Agw.Host.Tests;

public sealed class ExtensionStartupTests
{
    private static readonly Guid ExtensionId = Guid.CreateVersion7();

    [Fact]
    public async Task Tools_EnabledCompiledExtension_ListsRegisteredTools()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.CreateVersion7().ToString("N"));
        var installed = Path.Combine(root, "installed");
        try
        {
            await WriteExtensionAsync(installed, "1.0.0");
            await using var host = await RunningHost.StartAsync(root, installed);
            await host.ToggleAsync(true);
            await host.WaitForStateAsync("Active");

            using var response = await host.Client.GetAsync("/api/tools", TestContext.Current.CancellationToken);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)
            );
            var tools = document
                .RootElement.GetProperty("data")
                .EnumerateArray()
                .Where(tool =>
                    tool.TryGetProperty("extensionId", out var id) && id.GetString() == ExtensionId.ToString()
                )
                .Select(tool => tool.GetProperty("extensionToolId").GetString())
                .Order()
                .ToArray();

            Assert.Equal(new[] { "calendar-admin", "calendar-lookup" }, tools);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task OpenApi_EnabledCompiledExtension_ExposesValidatedRequestAndResponseSchemas()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.CreateVersion7().ToString("N"));
        var installed = Path.Combine(root, "installed");
        try
        {
            await WriteExtensionAsync(installed, "1.0.0");
            await using var host = await RunningHost.StartAsync(root, installed, development: true);
            await host.ToggleAsync(true);
            await host.WaitForStateAsync("Active");

            using var response = await host.Client.GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken);

            Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}\n{host.Diagnostics}");
            using var document = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)
            );
            var operation = document
                .RootElement.GetProperty("paths")
                .GetProperty("/api/ext/calendar/echo")
                .GetProperty("post");
            var request = operation
                .GetProperty("requestBody")
                .GetProperty("content")
                .GetProperty("application/json")
                .GetProperty("schema");
            Assert.Equal(
                "string",
                request.GetProperty("properties").GetProperty("value").GetProperty("type").GetString()
            );
            var result = operation
                .GetProperty("responses")
                .GetProperty("200")
                .GetProperty("content")
                .GetProperty("application/json")
                .GetProperty("schema")
                .GetProperty("properties")
                .GetProperty("data");
            Assert.Equal(
                "string",
                result.GetProperty("properties").GetProperty("value").GetProperty("type").GetString()
            );
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Startup_OfflineUpdateAndRemoval_RestoresEnablementAndScansOnce()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.CreateVersion7().ToString("N"));
        var installed = Path.Combine(root, "installed");
        try
        {
            await WriteExtensionAsync(installed, "1.0.0");
            await using (var first = await RunningHost.StartAsync(root, installed))
            {
                var status = Assert.Single(await first.ListAsync());
                Assert.False(status.GetProperty("isEnabled").GetBoolean());
                Assert.Equal("Disabled", status.GetProperty("state").GetString());
                Assert.Equal("1.0.0", status.GetProperty("currentVersion").GetString());
                Assert.Empty(await first.CatalogAsync());
                await first.ToggleAsync(true);
                await first.WaitForStateAsync("Active");
                var release = Assert.Single(await first.CatalogAsync());
                await first.AssertPackageAsync(release);
                await WriteExtensionAsync(installed, "1.0.1");
                await first.ToggleAsync(false);
                await first.WaitForStateAsync("Disabled");
                await first.AssertPackageUnavailableAsync(release);
                await first.ToggleAsync(true);
                await first.WaitForStateAsync("Active");
                Assert.Equal("1.0.0", Assert.Single(await first.ListAsync()).GetProperty("currentVersion").GetString());
                Assert.Equal(
                    release.GetProperty("releaseHash").GetString(),
                    Assert.Single(await first.CatalogAsync()).GetProperty("releaseHash").GetString()
                );
            }
            await using (var updated = await RunningHost.StartAsync(root, installed))
            {
                await updated.WaitForStateAsync("Active");
                Assert.Equal(
                    "1.0.1",
                    Assert.Single(await updated.ListAsync()).GetProperty("currentVersion").GetString()
                );
            }
            Directory.Delete(installed, recursive: true);
            await using (var removed = await RunningHost.StartAsync(root, installed))
                Assert.Empty(await removed.ListAsync());
            await WriteExtensionAsync(installed, "1.0.1");
            await using (var restored = await RunningHost.StartAsync(root, installed))
            {
                await restored.WaitForStateAsync("Active");
                Assert.True(Assert.Single(await restored.ListAsync()).GetProperty("isEnabled").GetBoolean());
            }
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Startup_SharedEnablement_PropagatesAcrossHosts()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.CreateVersion7().ToString("N"));
        var installed = Path.Combine(root, "installed");
        var databasePath = Path.Combine(root, "shared.db");
        try
        {
            await WriteExtensionAsync(
                installed,
                "1.0.0",
                "Agw.Extensions.Runtime.Tests.Fixture.TestExtensionWithoutTools"
            );
            await using var first = await RunningHost.StartAsync(
                Path.Combine(root, "first"),
                installed,
                databasePath: databasePath
            );
            await using var second = await RunningHost.StartAsync(
                Path.Combine(root, "second"),
                installed,
                databasePath: databasePath
            );
            Assert.False(Assert.Single(await first.ListAsync()).GetProperty("isEnabled").GetBoolean());
            Assert.False(Assert.Single(await second.ListAsync()).GetProperty("isEnabled").GetBoolean());

            await first.ToggleAsync(true);
            await first.WaitForStateAsync("Active");
            await second.WaitForStateAsync("Active");
            Assert.Equal(
                Assert.Single(await first.ListAsync()).GetProperty("settingsVersion").GetInt64(),
                Assert.Single(await second.ListAsync()).GetProperty("settingsVersion").GetInt64()
            );

            await second.ToggleAsync(false);
            await first.WaitForStateAsync("Disabled");
            await second.WaitForStateAsync("Disabled");
            Assert.False(Assert.Single(await first.ListAsync()).GetProperty("isEnabled").GetBoolean());
            Assert.Equal(
                Assert.Single(await first.ListAsync()).GetProperty("settingsVersion").GetInt64(),
                Assert.Single(await second.ListAsync()).GetProperty("settingsVersion").GetInt64()
            );
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Startup_InvalidChecksum_ExitsBeforeServing()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.CreateVersion7().ToString("N"));
        var installed = Path.Combine(root, "installed");
        try
        {
            await WriteExtensionAsync(installed, "1.0.0");
            await File.WriteAllTextAsync(
                Path.Combine(installed, "calendar/server/Agw.Extensions.Runtime.TestExtension.dll"),
                "invalid checksum",
                TestContext.Current.CancellationToken
            );
            await using var host = await RunningHost.StartAsync(root, installed, expectFailure: true);
            Assert.NotEqual(0, host.ExitCode);
            Assert.Contains("checksum", host.Diagnostics, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Enable_DuringAnEarlierDisable_UsesTheLatestSettingAfterDrainTimeout()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.CreateVersion7().ToString("N"));
        var installed = Path.Combine(root, "installed");
        try
        {
            await WriteExtensionAsync(installed, "1.0.0");
            await using var host = await RunningHost.StartAsync(
                root,
                installed,
                drainTimeout: TimeSpan.FromMilliseconds(200)
            );
            await host.ToggleAsync(true);
            await host.WaitForStateAsync("Active");
            var release = Assert.Single(await host.CatalogAsync());
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/ext/calendar/stream");
            request.Headers.Add("X-Agw-Extension-Id", ExtensionId.ToString("D"));
            using var response = await host.Client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                TestContext.Current.CancellationToken
            );
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken);
            var firstByte = new byte[1];
            Assert.Equal(1, await stream.ReadAsync(firstByte, TestContext.Current.CancellationToken));
            await host.ToggleAsync(false);
            await host.WaitForStateAsync("Draining");
            await host.AssertPackageUnavailableAsync(release);
            await host.ToggleAsync(true);
            using var remaining = new StreamReader(stream);
            Assert.Contains("\"chunk\":2", await remaining.ReadToEndAsync(TestContext.Current.CancellationToken));
            await host.WaitForStateAsync("Active");
            var current = Assert.Single(await host.ListAsync());
            Assert.True(current.GetProperty("isEnabled").GetBoolean());
            Assert.Equal(JsonValueKind.Null, current.GetProperty("errorCode").ValueKind);
            Assert.True(current.GetProperty("settingsVersion").GetInt64() >= 3);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static async Task WriteExtensionAsync(
        string installed,
        string version,
        string entryType = "Agw.Extensions.Runtime.Tests.Fixture.TestExtension"
    )
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "ExtensionFixture");
        Assert.True(Directory.Exists(fixture), $"Compiled fixture was not found: {fixture}");
        var directory = Path.Combine(installed, "calendar");
        Directory.CreateDirectory(directory);
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var source in Directory.EnumerateFiles(fixture, "*", SearchOption.AllDirectories))
        {
            var relative = "server/" + Path.GetRelativePath(fixture, source).Replace('\\', '/');
            var bytes = await File.ReadAllBytesAsync(source, TestContext.Current.CancellationToken);
            var target = Path.Combine(directory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await File.WriteAllBytesAsync(target, bytes, TestContext.Current.CancellationToken);
            files.Add(relative, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        }
        var manifest = JsonSerializer.Serialize(
            new
            {
                manifestVersion = 2,
                id = ExtensionId.ToString("D"),
                name = "calendar",
                displayName = "Calendar",
                version,
                targets = 3,
                server = new
                {
                    apiVersion = 1,
                    entryAssembly = "server/Agw.Extensions.Runtime.TestExtension.dll",
                    entryType,
                },
                desktop = new { apiVersion = 1 },
                files,
            }
        );
        await File.WriteAllTextAsync(
            Path.Combine(directory, "manifest.json"),
            manifest,
            TestContext.Current.CancellationToken
        );
    }

    private sealed class RunningHost : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly ServiceProvider _services;
        private readonly ConcurrentQueue<string> _output = new();
        private readonly Task _stdout;
        private readonly Task _stderr;
        public HttpClient Client { get; }
        public int ExitCode => _process.ExitCode;
        public string Diagnostics => string.Join(Environment.NewLine, _output);

        private RunningHost(Process process, Uri address)
        {
            _process = process;
            var services = new ServiceCollection();
            services
                .AddHttpClient("host", client => client.BaseAddress = address)
                .ConfigurePrimaryHttpMessageHandler(() =>
                    new HttpClientHandler { CookieContainer = new CookieContainer() }
                );
            _services = services.BuildServiceProvider();
            Client = _services.GetRequiredService<IHttpClientFactory>().CreateClient("host");
            _stdout = ReadOutputAsync(process.StandardOutput);
            _stderr = ReadOutputAsync(process.StandardError);
        }

        public static async Task<RunningHost> StartAsync(
            string root,
            string installed,
            bool expectFailure = false,
            TimeSpan? drainTimeout = null,
            string? databasePath = null,
            bool development = false
        )
        {
            Directory.CreateDirectory(root);
            var repository = new DirectoryInfo(AppContext.BaseDirectory);
            while (!File.Exists(Path.Combine(repository.FullName, "Agw.slnx")))
                repository = repository.Parent ?? throw new Xunit.Sdk.XunitException("Repository root was not found.");
            var binary = Path.Combine(AppContext.BaseDirectory, "agw-server.dll");
            Assert.True(File.Exists(binary), $"Build Standalone Host before running this test: {binary}");
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var address = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}");
            listener.Stop();
            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = repository.FullName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            start.ArgumentList.Add(binary);
            foreach (
                var pair in new Dictionary<string, string>
                {
                    ["environment"] = development ? "Development" : "Production",
                    ["urls"] = address.ToString(),
                    ["AgwDataDir"] = Path.Combine(root, "data"),
                    ["AgwLogDir"] = Path.Combine(root, "logs"),
                    ["Database:Provider"] = "Sqlite",
                    ["Database:ConnectionString"] =
                        $"Data Source={databasePath ?? Path.Combine(root, "agw.db")};Pooling=False",
                    ["Execution:Provider"] = "InProcess",
                    ["DistributedLock:Provider"] = "inmemory",
                    ["Setup:AdminPassword"] = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24)),
                    ["Extensions:Directory"] = installed,
                    ["Extensions:StatePollInterval"] = "00:00:00.100",
                    ["Extensions:DrainTimeout"] = (drainTimeout ?? TimeSpan.FromSeconds(5)).ToString(),
                    ["Extensions:UnloadTimeout"] = "00:00:05",
                    ["Logging:LogLevel:Default"] = "Warning",
                    ["Serilog:MinimumLevel:Default"] = "Information",
                }
            )
                start.ArgumentList.Add($"--{pair.Key}={pair.Value}");
            var host = new RunningHost(
                Process.Start(start) ?? throw new Xunit.Sdk.XunitException("Host could not start."),
                address
            );
            try
            {
                if (expectFailure)
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                        TestContext.Current.CancellationToken
                    );
                    timeout.CancelAfter(TimeSpan.FromSeconds(45));
                    await host._process.WaitForExitAsync(timeout.Token);
                    await Task.WhenAll(host._stdout, host._stderr);
                }
                else
                {
                    await host.WaitUntilReadyAsync();
                    using var response = await host.Client.GetAsync(
                        "/api/auth/antiforgery",
                        TestContext.Current.CancellationToken
                    );
                    response.EnsureSuccessStatusCode();
                    using var json = JsonDocument.Parse(
                        await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)
                    );
                    host.Client.DefaultRequestHeaders.Add(
                        "X-CSRF-TOKEN",
                        json.RootElement.GetProperty("data").GetProperty("requestToken").GetString()
                    );
                }
                return host;
            }
            catch
            {
                await host.DisposeAsync();
                throw;
            }
        }

        public async Task<JsonElement[]> ListAsync()
        {
            using var response = await Client.GetAsync("/api/extensions", TestContext.Current.CancellationToken);
            Assert.True(
                response.IsSuccessStatusCode,
                $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}\n{Diagnostics}"
            );
            using var json = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)
            );
            return json.RootElement.GetProperty("data").EnumerateArray().Select(item => item.Clone()).ToArray();
        }

        public async Task ToggleAsync(bool enabled)
        {
            using var response = await Client.PostAsJsonAsync(
                $"/api/extensions/{(enabled ? "enable" : "disable")}",
                new { extensionId = ExtensionId },
                TestContext.Current.CancellationToken
            );
            Assert.True(
                response.IsSuccessStatusCode,
                $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}\n{Diagnostics}"
            );
        }

        public async Task<JsonElement[]> CatalogAsync()
        {
            using var response = await Client.GetAsync(
                "/api/extensions/catalog",
                TestContext.Current.CancellationToken
            );
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)
            );
            var catalog = json.RootElement.GetProperty("data");
            Assert.InRange(catalog.GetProperty("revision").GetInt64(), 0, 9_007_199_254_740_991L);
            return catalog.GetProperty("extensions").EnumerateArray().Select(item => item.Clone()).ToArray();
        }

        public async Task AssertPackageAsync(JsonElement release)
        {
            using var response = await Client.GetAsync(PackageUrl(release), TestContext.Current.CancellationToken);
            response.EnsureSuccessStatusCode();
            var bytes = await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
            Assert.Equal(
                release.GetProperty("packageHash").GetString(),
                Convert.ToHexStringLower(SHA256.HashData(bytes))
            );
            using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            var manifestEntry = zip.GetEntry("manifest.json");
            Assert.NotNull(manifestEntry);
            await using var manifest = manifestEntry.Open();
            Assert.Equal(
                release.GetProperty("releaseHash").GetString(),
                Convert.ToHexStringLower(await SHA256.HashDataAsync(manifest, TestContext.Current.CancellationToken))
            );
        }

        public async Task AssertPackageUnavailableAsync(JsonElement release)
        {
            Assert.Empty(await CatalogAsync());
            using var response = await Client.GetAsync(PackageUrl(release), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        private static string PackageUrl(JsonElement release) =>
            $"/api/extensions/package?extensionId={ExtensionId:D}&releaseHash={release.GetProperty("releaseHash").GetString()}";

        public async Task WaitForStateAsync(string expected)
        {
            var started = Stopwatch.GetTimestamp();
            while (Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(30))
            {
                var status = Assert.Single(await ListAsync());
                if (status.GetProperty("state").GetString() == expected)
                    return;
                Assert.True(status.GetProperty("state").GetString() != "Faulted", $"{status}\n{Diagnostics}");
                await Task.Delay(50, TestContext.Current.CancellationToken);
            }
            Assert.Fail($"Expected {expected}.\n{Diagnostics}");
        }

        private async Task WaitUntilReadyAsync()
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(90));
            while (true)
            {
                Assert.False(_process.HasExited, Diagnostics);
                try
                {
                    using var response = await Client.GetAsync("/api/health/ready", timeout.Token);
                    if (response.IsSuccessStatusCode)
                        return;
                    await Task.Delay(50, timeout.Token);
                }
                catch (HttpRequestException) when (!_process.HasExited)
                {
                    await Task.Delay(50, timeout.Token);
                }
            }
        }

        private async Task ReadOutputAsync(StreamReader reader)
        {
            while (await reader.ReadLineAsync() is { } line)
                _output.Enqueue(line);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
            await Task.WhenAll(_stdout, _stderr);
            _process.Dispose();
            await _services.DisposeAsync();
        }
    }
}
