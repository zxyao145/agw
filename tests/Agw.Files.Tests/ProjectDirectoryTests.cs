using System.Diagnostics;
using Agw.Files.Abstracts;
using Agw.Files.Application.Files;
using Agw.Files.Application.Storage.Resolver;
using Agw.Files.Exceptions;
using Agw.Files.Services;
using Agw.Shared.Exceptions;
using Agw.Shared.Runtime;
using Agw.Shared.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Files.Tests;

public sealed class ProjectDirectoryTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("agw-directories-").FullName;
    private readonly Guid _projectId = Guid.NewGuid();
    private readonly Guid _directoryId = Guid.NewGuid();
    private readonly ConfigurationProvider _configuration;
    private readonly ServiceProvider _services;
    private readonly ProjectScopedFileSystemResolver _resolver;
    private readonly FileAppService _files;
    private string Primary => Path.Combine(_root, "primary");
    private string Additional => Path.Combine(_root, "additional");
    private CancellationToken Token => TestContext.Current.CancellationToken;

    public ProjectDirectoryTests()
    {
        Directory.CreateDirectory(Primary);
        Directory.CreateDirectory(Additional);
        _configuration = new ConfigurationProvider
        {
            Value = new ProjectFileSystemConfiguration(
                "Project",
                Primary,
                "owner",
                [new ProjectWorkspaceDirectory(_directoryId, Additional)]
            ),
        };
        _services = new ServiceCollection()
            .AddSingleton<IProjectFileSystemConfigurationProvider>(_configuration)
            .BuildServiceProvider();
        _resolver = new ProjectScopedFileSystemResolver(
            _services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ProjectScopedFileSystemResolver>.Instance,
            TimeProvider.System
        );
        _files = new FileAppService(
            _resolver,
            new GitCommandService(NullLogger<GitCommandService>.Instance),
            NullLogger<FileAppService>.Instance
        );
    }

    [Fact]
    public async Task ReadAndSearchAsync_SameFileNames_UseSelectedDirectory()
    {
        await File.WriteAllTextAsync(Path.Combine(Primary, "README.md"), "primary", Token);
        await File.WriteAllTextAsync(Path.Combine(Additional, "README.md"), "additional", Token);

        Assert.Equal("primary", (await _files.ReadAsync(_projectId, "README.md", Token)).Value);
        Assert.Equal("additional", (await _files.ReadAsync(_projectId, "README.md", Token, _directoryId)).Value);
        var search = await _files.SearchAsync(_projectId, "", "README", 10, true, Token, _directoryId);
        Assert.Equal("README.md", Assert.Single(search.Value!.Results).RelativePath);
        var list = await _files.ListAsync(_projectId, "", false, false, Token, _directoryId);
        Assert.Equal("README.md", Assert.Single(list.Value!.Items).Path);
        Assert.Equal(
            FileOperationStatus.NotFound,
            (await _files.ReadAsync(_projectId, "README.md", Token, Guid.NewGuid())).Status
        );
    }

    [Theory]
    [InlineData("../primary/README.md")]
    [InlineData("../../README.md")]
    public async Task ReadAsync_PathOutsideSelectedDirectory_IsRejected(string path)
    {
        await Assert.ThrowsAsync<AgwFilesException>(() => _files.ReadAsync(_projectId, path, Token, _directoryId));
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("nested/..")]
    public async Task DeleteAsync_DirectoryRoot_IsProtected(string path)
    {
        var result = await _files.DeleteAsync(_projectId, path, Token, _directoryId);
        Assert.Equal(FileOperationStatus.InvalidRequest, result.Status);
        Assert.True(Directory.Exists(Additional));
    }

    [Fact]
    public async Task ResolveAsync_RemovedDirectory_IsAvailableOnlyToOriginalExecutionSnapshot()
    {
        var snapshot = ProjectWorkspacePaths.CreateSnapshot(
            _projectId,
            Primary,
            _configuration.Value!.AdditionalDirectories
        );
        var original = await _resolver.ResolveSnapshotAsync(_projectId, snapshot, _directoryId, Token);
        Assert.Equal(0, _configuration.ConfigurationReads);
        Assert.Equal(1, _configuration.OwnerReads);
        _configuration.Value = _configuration.Value with { AdditionalDirectories = [] };
        _resolver.Invalidate(_projectId);

        Assert.Null(await _resolver.ResolveAsync(_projectId, _directoryId, Token));
        Assert.NotNull(await _resolver.ResolveSnapshotAsync(_projectId, snapshot, _directoryId, Token));
        Assert.NotNull(original);
        _configuration.Value = null;
        Assert.Null(await _resolver.ResolveSnapshotAsync(_projectId, snapshot, _directoryId, Token));
    }

    [Fact]
    public async Task ResolveAsync_UnavailableAdditionalDirectory_FailsWithoutPrimaryFallback()
    {
        await _resolver.ResolveAsync(_projectId, _directoryId, Token);
        Directory.Delete(Additional);
        var error = await Assert.ThrowsAsync<AgwException>(() =>
            _resolver.ResolveAsync(_projectId, _directoryId, Token)
        );
        Assert.Contains(Additional, error.Message);
        Assert.True(Directory.Exists(Primary));
    }

    [Fact]
    public async Task GitOperations_TwoDirectoriesInOneRepository_StayWithinSelectedDirectory()
    {
        await GitAsync("init", "--quiet");
        await GitAsync("config", "core.autocrlf", "false");
        await File.WriteAllTextAsync(Path.Combine(Primary, "README.md"), "primary\n", Token);
        await File.WriteAllTextAsync(Path.Combine(Additional, "README.md"), "additional\n", Token);
        await GitAsync("add", ".");
        await GitAsync(
            "-c",
            "user.name=Test",
            "-c",
            "user.email=test@example.invalid",
            "-c",
            "commit.gpgsign=false",
            "commit",
            "--quiet",
            "-m",
            "initial"
        );
        await File.WriteAllTextAsync(Path.Combine(Primary, "README.md"), "primary changed\n", Token);
        await File.WriteAllTextAsync(Path.Combine(Additional, "README.md"), "additional changed\n", Token);

        var list = await _files.ListAsync(_projectId, "", true, true, Token, _directoryId);
        Assert.Equal("README.md", Assert.Single(list.Value!.Items).Path);
        var diff = await _files.DiffAsync(_projectId, "README.md", null, Token, _directoryId);
        Assert.Contains("additional changed", diff.Value!.Diff);
        Assert.DoesNotContain("primary changed", diff.Value.Diff);
        Assert.Equal(
            FileOperationStatus.Success,
            (await _files.StageAsync(_projectId, "README.md", Token, _directoryId)).Status
        );
        Assert.Equal("additional/README.md", (await GitAsync("diff", "--cached", "--name-only")).Trim());
        Assert.Equal(
            FileOperationStatus.Success,
            (await _files.UnstageAsync(_projectId, "README.md", Token, _directoryId)).Status
        );
        Assert.Empty((await GitAsync("diff", "--cached", "--name-only")).Trim());
        Assert.Equal(
            FileOperationStatus.Success,
            (await _files.ResetAsync(_projectId, "README.md", Token, _directoryId)).Status
        );
        Assert.Equal("additional\n", await File.ReadAllTextAsync(Path.Combine(Additional, "README.md"), Token));
        Assert.Equal("primary changed\n", await File.ReadAllTextAsync(Path.Combine(Primary, "README.md"), Token));
    }

    private async Task<string> GitAsync(params string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = _root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(Token);
        var error = process.StandardError.ReadToEndAsync(Token);
        await process.WaitForExitAsync(Token);
        Assert.True(process.ExitCode == 0, await error);
        return await output;
    }

    public void Dispose()
    {
        _services.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private sealed class ConfigurationProvider : IProjectFileSystemConfigurationProvider
    {
        public ProjectFileSystemConfiguration? Value { get; set; }
        public int ConfigurationReads { get; private set; }
        public int OwnerReads { get; private set; }

        public Task<ProjectFileSystemConfiguration?> GetAsync(
            Guid projectId,
            CancellationToken cancellationToken = default
        )
        {
            ConfigurationReads++;
            return Task.FromResult(Value);
        }

        public Task<string?> GetOwnerUserIdAsync(Guid projectId, CancellationToken cancellationToken)
        {
            OwnerReads++;
            return Task.FromResult(Value?.OwnerUserId);
        }
    }
}
