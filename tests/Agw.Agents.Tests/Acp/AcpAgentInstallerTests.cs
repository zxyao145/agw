using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agw.Agents.Application.Acp;
using Agw.Agents.ExternalAgents.Acp;
using Agw.Shared.Exceptions;
using Microsoft.Extensions.DependencyInjection;
using PiAgentSdk.Internal;
using SharpCompress.Compressors.BZip2;

namespace Agw.Agents.Tests.Acp;

public sealed class AcpAgentInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "Acp", Guid.NewGuid().ToString());
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    internal static AcpRegistryAgent Binary(Uri uri, byte[] bytes, string cmd, string? sha = null) =>
        new(
            "goose",
            "goose",
            "1.60.2",
            "",
            null,
            null,
            null,
            new(
                new Dictionary<string, AcpBinaryTarget>
                {
                    [AcpPlatform.Current!] = new(
                        uri,
                        cmd,
                        ["acp"],
                        null,
                        sha ?? Convert.ToHexString(SHA256.HashData(bytes))
                    ),
                },
                null,
                null
            )
        );

    [Theory]
    [InlineData("..", "1.0.0")]
    [InlineData("goose", "../bin")]
    public async Task Install_InvalidRegistryIdentity_ReportsInstallFailure(string agentId, string version)
    {
        await using var server = await AcpTestRegistryServer.StartAsync();
        byte[] bytes = [42];
        var entry = Binary(server.AddFile("/goose", bytes), bytes, "goose") with { Id = agentId, Version = version };
        using var services = AcpRegistryClientTests.Services(server.RegistryUrl, root: _root);

        var error = await Assert.ThrowsAsync<AgwException>(() =>
            services.GetRequiredService<IAcpAgentInstaller>().InstallAsync(entry, _ct)
        );

        Assert.Equal(ErrorCodes.AcpInstallFailed.Code, error.Code);
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task Install_TarBz2WithSha_WritesManifestAndMarksExecutable()
    {
        await using var server = await AcpTestRegistryServer.StartAsync();
        var bytes = Tar("goose");
        using var services = AcpRegistryClientTests.Services(server.RegistryUrl, root: _root);
        var installer = services.GetRequiredService<IAcpAgentInstaller>();
        var entry = Binary(server.AddFile("/goose.tar.bz2", bytes), bytes, "./goose");
        var result = await installer.InstallAsync(entry, _ct);
        Assert.False(result.AlreadyInstalled);
        var installed = services.GetRequiredService<IAcpInstalledAgentCatalog>().Find("goose", entry.Version);
        Assert.Equal("native", installed.Manifest.Runtime);
        Assert.Equal("goose", installed.Manifest.Executable);
        Assert.Equal(["acp"], installed.Manifest.Args);
        if (!OperatingSystem.IsWindows())
            Assert.True(File.GetUnixFileMode(installed.ExecutablePath).HasFlag(UnixFileMode.UserExecute));
        Assert.True((await installer.InstallAsync(entry, _ct)).AlreadyInstalled);
        Assert.Equal(
            installed.Manifest.InstalledAt,
            services.GetRequiredService<IAcpInstalledAgentCatalog>().Find("goose", null).Manifest.InstalledAt
        );
    }

    [Fact]
    public async Task Install_ZipWithWindowsCmd_NormalizesExecutable()
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
            zip.CreateEntry("goose-package/goose.exe");
        var bytes = stream.ToArray();
        await using var server = await AcpTestRegistryServer.StartAsync();
        using var services = AcpRegistryClientTests.Services(server.RegistryUrl, root: _root);
        var installer = services.GetRequiredService<IAcpAgentInstaller>();
        await installer.InstallAsync(
            Binary(server.AddFile("/release.zip", bytes), bytes, "./goose-package\\goose.exe"),
            _ct
        );
        Assert.Equal(
            "goose-package/goose.exe",
            services.GetRequiredService<IAcpInstalledAgentCatalog>().Find("goose", null).Manifest.Executable
        );
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("traversal")]
    [InlineData("link")]
    [InlineData("size")]
    [InlineData("missing-entry")]
    [InlineData("entry-type")]
    public async Task Install_InvalidRelease_RemovesPartialDirectory(string reason)
    {
        await using var server = await AcpTestRegistryServer.StartAsync();
        using var services = AcpRegistryClientTests.Services(server.RegistryUrl, root: _root, limit: 1024);
        var bytes = reason switch
        {
            "traversal" => Tar("../outside"),
            "link" => Tar("goose", "/outside"),
            "size" => new byte[1025],
            "missing-entry" => Tar("helper"),
            "entry-type" => Tar("goose", type: TarEntryType.Fifo),
            _ => Tar("goose"),
        };
        var entry = Binary(server.AddFile("/goose.tar.bz2", bytes), bytes, "goose", reason == "hash" ? "ffff" : null);
        var error = await Assert.ThrowsAsync<AgwException>(() =>
            services.GetRequiredService<IAcpAgentInstaller>().InstallAsync(entry, _ct)
        );
        Assert.Equal(
            reason is "hash" or "size" ? ErrorCodes.AcpDownloadFailed.Code : ErrorCodes.AcpInstallFailed.Code,
            error.Code
        );
        if (reason == "hash")
            Assert.StartsWith("SHA-256 mismatch for", error.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_root, "goose")));
    }

    [Fact]
    public async Task Install_ZipSymbolicLink_ValidatesTargetBeforePublishing()
    {
        using var archive = new MemoryStream();
        using (var zip = new ZipArchive(archive, ZipArchiveMode.Create, true))
        {
            var link = zip.CreateEntry("goose");
            link.ExternalAttributes = 0xa1ff << 16;
            using var stream = link.Open();
            stream.Write("../outside"u8);
        }
        var bytes = archive.ToArray();
        await using var server = await AcpTestRegistryServer.StartAsync();
        using var services = AcpRegistryClientTests.Services(server.RegistryUrl, root: _root);
        var entry = Binary(server.AddFile("/release.zip", bytes), bytes, "goose");
        var error = await Assert.ThrowsAsync<AgwException>(() =>
            services.GetRequiredService<IAcpAgentInstaller>().InstallAsync(entry, _ct)
        );
        Assert.Equal(ErrorCodes.AcpInstallFailed.Code, error.Code);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_root, entry.Id)));
    }

    [Theory]
    [InlineData(" ", "is missing its package")]
    [InlineData("invalid/package", "Invalid Python tool package name")]
    public async Task Install_InvalidUvxPackage_ReportsInstallFailure(string package, string message)
    {
        Assert.SkipWhen(!(await CommandUtil.FindAsync("uv", cancellationToken: _ct)).IsInstalled, "Requires uv.");
        await using var server = await AcpTestRegistryServer.StartAsync();
        using var services = AcpRegistryClientTests.Services(server.RegistryUrl, root: _root);
        var entry = new AcpRegistryAgent(
            "script-tool",
            "Test",
            "1.0.0",
            "",
            null,
            null,
            null,
            new(null, null, new(package, null, null))
        );

        var error = await Assert.ThrowsAsync<AgwException>(() =>
            services.GetRequiredService<IAcpAgentInstaller>().InstallAsync(entry, _ct)
        );

        Assert.Equal(ErrorCodes.AcpInstallFailed.Code, error.Code);
        Assert.Contains(message, error.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_root, entry.Id)));
    }

    [Fact]
    public async Task Install_SlowReleaseBody_ReportsDownloadTimeout()
    {
        await using var server = await AcpTestRegistryServer.StartAsync();
        using var services = AcpRegistryClientTests.Services(
            server.RegistryUrl,
            root: _root,
            downloadTimeout: TimeSpan.FromMilliseconds(200)
        );
        var entry = Binary(new Uri(server.RegistryUrl, "/slow-release"), [1], "agent");
        var error = await Assert.ThrowsAsync<AgwException>(() =>
            services.GetRequiredService<IAcpAgentInstaller>().InstallAsync(entry, _ct)
        );
        Assert.Equal(ErrorCodes.AcpDownloadFailed.Code, error.Code);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_root, entry.Id)));
    }

    [Fact]
    public async Task Install_RawBinaryUrlWithQuery_NamesFileByCmd()
    {
        await using var server = await AcpTestRegistryServer.StartAsync();
        byte[] bytes = [5, 42];
        using var services = AcpRegistryClientTests.Services(server.RegistryUrl, root: _root);
        var entry = Binary(server.AddFile("/download/?file=x", bytes), bytes, "./tool");
        var installed = await services.GetRequiredService<IAcpAgentInstaller>().InstallAsync(entry, _ct);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(installed.Directory, "tool"), _ct));
    }

    [Fact]
    public async Task Install_ConcurrentCalls_PublishOneCompleteInstallation()
    {
        await using var server = await AcpTestRegistryServer.StartAsync();
        byte[] bytes = [5, 42];
        var entry = Binary(server.AddFile("/download", bytes), bytes, "tool");
        using var services = AcpRegistryClientTests.Services(server.RegistryUrl, root: _root);
        var installer = services.GetRequiredService<IAcpAgentInstaller>();
        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => installer.InstallAsync(entry, _ct)));
        Assert.Single(results, result => !result.AlreadyInstalled);
        var version = Assert.Single(Directory.EnumerateDirectories(Path.Combine(_root, entry.Id)));
        Assert.Equal(entry.Version, Path.GetFileName(version));
        var catalog = services.GetRequiredService<IAcpInstalledAgentCatalog>();
        Assert.Equal(bytes, await File.ReadAllBytesAsync(catalog.Find(entry.Id, entry.Version).ExecutablePath, _ct));
    }

    [Fact]
    public async Task Install_Npx_ResolvesBinEntrypoint()
    {
        var package = await (await AcpTestAgent.EnsureInstalledAsync()).PackAsync();
        var env = new Dictionary<string, string> { { "NODE_OPTIONS", "ignored" }, { "KEEP", "1" } };
        var entry = new AcpRegistryAgent(
            "agw-acp-test-agent",
            "Test",
            "1.0.0",
            "",
            null,
            null,
            null,
            new(null, new(package, null, env), null)
        );
        using var services = AcpRegistryClientTests.Services(
            new("https://example.com"),
            root: _root,
            downloadTimeout: TimeSpan.FromMilliseconds(1)
        );
        await services.GetRequiredService<IAcpAgentInstaller>().InstallAsync(entry, _ct);
        var installed = services.GetRequiredService<IAcpInstalledAgentCatalog>().Find(entry.Id, null);
        Assert.Equal("node", installed.Manifest.Runtime);
        Assert.Equal("node_modules/agw-acp-test-agent/acp-test-agent.mjs", installed.Manifest.Executable);
        Assert.Equal("KEEP", Assert.Single(installed.Manifest.Env).Key);
    }

    [Fact]
    public async Task Install_UvxWithManagedPython_RunsAfterMovingVersionDirectory()
    {
        await using var server = await AcpTestRegistryServer.StartAsync();
        var dependency = AddWheel(
            server,
            "wheel-support",
            new() { ["wheel_support/__init__.py"] = "label = 'wheel extra'\n" }
        );
        var tool = AddWheel(
            server,
            "wheel-tool",
            new()
            {
                ["wheel_tool/__init__.py"] = "from wheel_support import label\n\ndef main():\n    print(label)\n",
                ["wheel_tool-1.0.0.dist-info/entry_points.txt"] = "[console_scripts]\nwheel-tool=wheel_tool:main\n",
            },
            "Provides-Extra: test\nRequires-Dist: wheel-support==1.0.0 ; extra == 'test'\n"
        );
        await ConfigureLocalUvAsync(server, tool, dependency);
        var entry = new AcpRegistryAgent(
            "wheel-tool",
            "Test",
            "1.0.0",
            "",
            null,
            null,
            null,
            new(null, null, new("wheel-tool[test]==1.0.0", null, null))
        );
        using var services = AcpRegistryClientTests.Services(server.RegistryUrl, root: _root);
        await services.GetRequiredService<IAcpAgentInstaller>().InstallAsync(entry, _ct);
        var installed = services.GetRequiredService<IAcpInstalledAgentCatalog>().Find(entry.Id, null);
        var moved = Path.Combine(_root, "moved version");
        Directory.Move(installed.Directory, moved);
        Assert.Equal("wheel extra", await RunInstalledAsync(moved, installed.Manifest));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Install_UvxWheelDataScript_RunsAfterMovingVersionDirectory(bool pythonScript)
    {
        Assert.SkipWhen(!pythonScript && OperatingSystem.IsWindows(), "Requires a POSIX shell script.");
        await using var server = await AcpTestRegistryServer.StartAsync();
        var script = pythonScript
            ? "#!python\nimport sys\nfrom pathlib import Path\nprint(Path(sys.argv[0]).name, sys.argv[1])\n"
            : "#!/bin/sh\nprintf '%s\\n' \"$1\"\n";
        var wheel = AddWheel(server, "script-tool", new() { ["script_tool-1.0.0.data/scripts/script-tool"] = script });
        await ConfigureLocalUvAsync(server, wheel);
        var entry = new AcpRegistryAgent(
            "script-tool",
            "script-tool",
            "1.0.0",
            "",
            null,
            null,
            null,
            new(null, null, new($"script-tool @ {wheel.AbsoluteUri}", ["relocated argument"], null))
        );
        using var services = AcpRegistryClientTests.Services(
            server.RegistryUrl,
            root: Path.Combine(_root, "stage with 'quotes'")
        );
        await services.GetRequiredService<IAcpAgentInstaller>().InstallAsync(entry, _ct);
        var installed = services.GetRequiredService<IAcpInstalledAgentCatalog>().Find(entry.Id, null);
        var moved = Path.Combine(_root, "published version");
        Directory.Move(installed.Directory, moved);
        Assert.Equal(
            pythonScript ? "script-tool relocated argument" : "relocated argument",
            await RunInstalledAsync(moved, installed.Manifest)
        );
    }

    private async Task ConfigureLocalUvAsync(AcpTestRegistryServer server, params Uri[] wheels)
    {
        Assert.SkipWhen(!(await CommandUtil.FindAsync("uv", cancellationToken: _ct)).IsInstalled, "Requires uv.");
        DirectoryInfo? repository = new(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "Agw.slnx")))
            repository = repository.Parent;
        var mirror = repository is null ? null : Path.Combine(repository.FullName, ".scratch", "acp-uvx-python");
        Assert.SkipWhen(!Directory.Exists(mirror), "Requires a local Python 3.12 mirror in .scratch/acp-uvx-python.");

        var links = string.Join(
            '\n',
            wheels.Select(wheel => $"<a href=\"{wheel.AbsoluteUri}\">{wheel.Segments[^1]}</a>")
        );
        var index = server.AddFile("/wheels/index.html", Encoding.UTF8.GetBytes(links), "text/html");
        var settings = $"""
            python-install-mirror = {JsonSerializer.Serialize(new Uri(mirror!).AbsoluteUri)}
            cache-dir = {JsonSerializer.Serialize(Path.Combine(_root, "uv-cache"))}

            [pip]
            no-index = true
            find-links = [{JsonSerializer.Serialize(index.AbsoluteUri)}]
            """;
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "uv.toml"), settings, _ct);
        await File.WriteAllTextAsync(Path.Combine(_root, ".python-version"), "3.12\n", _ct);
    }

    private static Uri AddWheel(
        AcpTestRegistryServer server,
        string package,
        Dictionary<string, string> files,
        string extraMetadata = ""
    )
    {
        var dist = package.Replace('-', '_') + "-1.0.0";
        files[$"{dist}.dist-info/WHEEL"] =
            "Wheel-Version: 1.0\nGenerator: Agw.Agents.Tests\nRoot-Is-Purelib: true\nTag: py3-none-any\n";
        files[$"{dist}.dist-info/METADATA"] =
            $"Metadata-Version: 2.1\nName: {package}\nVersion: 1.0.0\n{extraMetadata}";
        using var content = new MemoryStream();
        using (var archive = new ZipArchive(content, ZipArchiveMode.Create, leaveOpen: true))
        {
            var records = new List<string>();
            foreach (var (name, text) in files)
            {
                var bytes = Encoding.UTF8.GetBytes(text);
                var digest = System.Buffers.Text.Base64Url.EncodeToString(SHA256.HashData(bytes));
                records.Add($"{name},sha256={digest},{bytes.Length}");
                using var stream = archive.CreateEntry(name).Open();
                stream.Write(bytes);
            }
            var record = $"{dist}.dist-info/RECORD";
            records.Add($"{record},,");
            using var output = archive.CreateEntry(record).Open();
            output.Write(Encoding.UTF8.GetBytes(string.Join('\n', records) + "\n"));
        }
        return server.AddFile($"/wheels/{dist}-py3-none-any.whl", content.ToArray());
    }

    private async Task<string> RunInstalledAsync(string directory, AcpInstallManifest manifest)
    {
        var start = new ProcessStartInfo(Path.Combine(directory, manifest.Executable))
        {
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in manifest.Args)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(_ct);
        var stderr = process.StandardError.ReadToEndAsync(_ct);
        await process.WaitForExitAsync(_ct);
        Assert.True(process.ExitCode == 0, await stderr);
        return (await stdout).Trim();
    }

    public static bool Unix => !OperatingSystem.IsWindows();

    [Fact]
    public async Task Install_DottedRoot_ResolvesArchiveLinksInsideVersionDirectory()
    {
        var parent = Directory.CreateDirectory(Path.Combine(_root, "sub")).FullName;
        var installRoot = Path.Combine(parent, "..", ".", "installed");
        byte[] payload = [42, 7];
        using var archive = new MemoryStream();
        using (var gzip = new GZipStream(archive, CompressionLevel.Optimal, true))
        using (var writer = new TarWriter(gzip))
        {
            writer.WriteEntry(
                new PaxTarEntry(TarEntryType.RegularFile, "bin/agent") { DataStream = new MemoryStream(payload) }
            );
            writer.WriteEntry(new PaxTarEntry(TarEntryType.HardLink, "agent-copy") { LinkName = "bin/agent" });
            if (!OperatingSystem.IsWindows())
                writer.WriteEntry(
                    new PaxTarEntry(TarEntryType.SymbolicLink, "links/agent") { LinkName = "../bin/agent" }
                );
        }
        var bytes = archive.ToArray();
        await using var server = await AcpTestRegistryServer.StartAsync();
        using var services = AcpRegistryClientTests.Services(server.RegistryUrl, root: installRoot);
        var entry = Binary(server.AddFile("/agent.tar.gz", bytes), bytes, "bin/agent");
        var result = await services.GetRequiredService<IAcpAgentInstaller>().InstallAsync(entry, _ct);
        Assert.Equal(payload, await File.ReadAllBytesAsync(Path.Combine(result.Directory, "agent-copy"), _ct));
        if (!OperatingSystem.IsWindows())
        {
            var link = new FileInfo(Path.Combine(result.Directory, "links", "agent"));
            Assert.Equal("../bin/agent", link.LinkTarget);
            Assert.Equal(payload, await File.ReadAllBytesAsync(link.FullName, _ct));
        }
    }

    [Theory(SkipUnless = nameof(Unix), Skip = "Requires Unix executable permissions.")]
    [InlineData(".tar.gz")]
    [InlineData(".zip")]
    public async Task Install_BundledHelper_PreservesOnlyOrdinaryExecuteBits(string extension)
    {
        var mode = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.SetUser | UnixFileMode.SetGroup;
        using var archive = new MemoryStream();
        const string script = "#!/bin/sh\nexit 42\n";
        if (extension == ".zip")
        {
            using var zip = new ZipArchive(archive, ZipArchiveMode.Create, true);
            zip.CreateEntry("agent");
            var helper = zip.CreateEntry("lib/helper");
            helper.ExternalAttributes = (0x8000 | (int)mode) << 16;
            using var stream = helper.Open();
            stream.Write(System.Text.Encoding.UTF8.GetBytes(script));
        }
        else
        {
            using var gzip = new GZipStream(archive, CompressionLevel.Optimal, true);
            using var writer = new TarWriter(gzip);
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "agent"));
            writer.WriteEntry(
                new PaxTarEntry(TarEntryType.RegularFile, "lib/helper")
                {
                    Mode = mode,
                    DataStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(script)),
                }
            );
        }
        var bytes = archive.ToArray();
        await using var server = await AcpTestRegistryServer.StartAsync();
        using var services = AcpRegistryClientTests.Services(server.RegistryUrl, root: _root);
        var entry = Binary(server.AddFile("/agent" + extension, bytes), bytes, "agent");
        var result = await services.GetRequiredService<IAcpAgentInstaller>().InstallAsync(entry, _ct);
        var path = Path.Combine(result.Directory, "lib", "helper");
        if (!OperatingSystem.IsWindows())
        {
            var installed = File.GetUnixFileMode(path);
            Assert.Equal(
                UnixFileMode.UserExecute | UnixFileMode.GroupExecute,
                installed & (mode | UnixFileMode.OtherExecute)
            );
            using var helper = Process.Start(new ProcessStartInfo(path) { UseShellExecute = false })!;
            await helper.WaitForExitAsync(_ct);
            Assert.Equal(42, helper.ExitCode);
        }
    }

    private static byte[] Tar(string name, string? link = null, TarEntryType type = TarEntryType.RegularFile)
    {
        using var result = new MemoryStream();
        using (
            var compressed = BZip2Stream.Create(result, SharpCompress.Compressors.CompressionMode.Compress, false, true)
        )
        using (var writer = new TarWriter(compressed))
        {
            var entry = new PaxTarEntry(link is null ? type : TarEntryType.SymbolicLink, name);
            if (link is not null)
                entry.LinkName = link;
            else if (type == TarEntryType.RegularFile)
                entry.DataStream = new MemoryStream([42]);
            writer.WriteEntry(entry);
        }
        return result.ToArray();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }
}
