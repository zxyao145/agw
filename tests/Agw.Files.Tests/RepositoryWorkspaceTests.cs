using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Agw.Files.Abstracts;
using Agw.Files.Infrastructure.Git;
using Agw.Files.Infrastructure.Storage;
using Agw.Shared.Coordination;
using Agw.Shared.Exceptions;
using CliWrap;
using CliWrap.Buffered;

namespace Agw.Files.Tests;

public sealed class RepositoryWorkspaceTests
{
    public static bool IsUnix => !OperatingSystem.IsWindows();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ExecuteAsync_TwoRepositories_CreatesWorktreesSwitchesAndMergesWithTwoParents()
    {
        using var fixture = await WorkspaceFixture.CreateAsync();
        await fixture.CloneAsync("alpha");
        await fixture.CloneAsync("beta");
        var state = await fixture.ExecuteAsync(
            "feature",
            new() { Name = "add-extensions", Repositories = ["alpha", "beta"] }
        );
        Assert.Equal(2, state.Repositories.Length);
        Assert.All(state.Repositories, repo => Assert.Equal("trunk", repo.DefaultBranch));
        var feature = Assert.Single(state.Features);
        Assert.Equal("261127-add-extensions", feature.Name);
        Assert.Equal(2, feature.Repositories.Length);
        Assert.All(feature.Repositories, repo => Assert.Equal(feature.Name, repo.Branch));
        var worktree = fixture.Worktree(feature.Name, "alpha");
        await File.WriteAllTextAsync(Path.Combine(worktree, "feature.txt"), "feature content", Token);
        await WorkspaceFixture.GitAsync(worktree, "add", ".");
        await WorkspaceFixture.GitAsync(worktree, "commit", "-m", "Add feature");
        state = await fixture.ExecuteAsync(
            "switch",
            new()
            {
                Name = "alpha",
                Feature = feature.Name,
                Branch = "other-feature",
                CreateBranch = true,
            }
        );
        Assert.Equal("other-feature", state.Features[0].Repositories[0].Branch);
        await fixture.ExecuteAsync(
            "switch",
            new()
            {
                Name = "alpha",
                Feature = feature.Name,
                Branch = feature.Name,
            }
        );
        await fixture.ExecuteAsync("merge", new() { Name = "alpha", Feature = feature.Name });
        Assert.Equal(
            "feature content",
            await File.ReadAllTextAsync(Path.Combine(fixture.Root, "alpha", "feature.txt"), Token)
        );
        var parents = await WorkspaceFixture.GitAsync(
            Path.Combine(fixture.Root, "alpha"),
            "show",
            "-s",
            "--format=%P",
            "HEAD"
        );
        Assert.Equal(2, parents.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Equal(
            "trunk",
            await WorkspaceFixture.GitAsync(Path.Combine(fixture.Root, "alpha"), "branch", "--show-current")
        );
        Assert.False(File.Exists(Path.Combine(fixture.Root, "beta", "feature.txt")));
        var ignore = await File.ReadAllTextAsync(Path.Combine(fixture.Root, ".gitignore"), Token);
        Assert.Contains("existing-rule", ignore);
        Assert.Contains("/.worktrees/", ignore);
        await fixture.CloneAsync("docs");
        state = await fixture.ExecuteAsync(
            "feature",
            new()
            {
                Name = "add-extensions",
                Feature = feature.Name,
                Repositories = ["docs"],
            }
        );
        Assert.Equal(3, state.Features[0].Repositories.Length);
    }

    [Fact]
    public async Task ExecuteAsync_DirtyOrUnlockedMain_RejectsOperationsWithoutChangingBranches()
    {
        using var fixture = await WorkspaceFixture.CreateAsync();
        await fixture.CloneAsync("alpha");
        var state = await fixture.ExecuteAsync("feature", new() { Name = "feature", Repositories = ["alpha"] });
        var feature = state.Features[0].Name;
        var worktree = fixture.Worktree(feature, "alpha");
        await File.WriteAllTextAsync(Path.Combine(worktree, "local.txt"), "uncommitted", Token);
        await Assert.ThrowsAsync<AgwException>(() =>
            fixture.ExecuteAsync(
                "switch",
                new()
                {
                    Name = "alpha",
                    Feature = feature,
                    Branch = "new",
                    CreateBranch = true,
                }
            )
        );
        await Assert.ThrowsAsync<AgwException>(() =>
            fixture.ExecuteAsync("merge", new() { Name = "alpha", Feature = feature })
        );
        Assert.Equal("uncommitted", await File.ReadAllTextAsync(Path.Combine(worktree, "local.txt"), Token));
        await WorkspaceFixture.GitAsync(Path.Combine(fixture.Root, "alpha"), "switch", "-c", "outside-change");
        var failure = await Assert.ThrowsAsync<AgwException>(() => fixture.Service.ReadAsync(fixture.Root, Token));
        Assert.Contains("must remain on its default branch", failure.Message);
        Assert.Equal(
            "outside-change",
            await WorkspaceFixture.GitAsync(Path.Combine(fixture.Root, "alpha"), "branch", "--show-current")
        );
    }

    [Fact]
    public async Task ExecuteAsync_InvalidOrDuplicateTargets_DoesNotCreatePartialFeature()
    {
        using var fixture = await WorkspaceFixture.CreateAsync();
        await fixture.CloneAsync("alpha");
        await Assert.ThrowsAsync<AgwException>(() =>
            fixture.ExecuteAsync("clone", new() { Name = "../escape", Url = fixture.Url })
        );
        await Assert.ThrowsAsync<AgwException>(() =>
            fixture.ExecuteAsync("clone", new() { Name = "local", Url = fixture.Root })
        );
        await Assert.ThrowsAsync<AgwException>(() => fixture.CloneAsync("alpha"));
        await Assert.ThrowsAsync<AgwException>(() =>
            fixture.ExecuteAsync("feature", new() { Name = "feature", Repositories = ["alpha", "missing"] })
        );
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, ".worktrees")));
        await Assert.ThrowsAsync<AgwException>(() =>
            fixture.ExecuteAsync(
                "switch",
                new()
                {
                    Name = "alpha",
                    Feature = "",
                    Branch = "anything",
                }
            )
        );
        Assert.Equal(
            "trunk",
            await WorkspaceFixture.GitAsync(Path.Combine(fixture.Root, "alpha"), "branch", "--show-current")
        );
    }

    [Fact]
    public async Task ExecuteAsync_MergeConflict_PreservesBothBranchesAndReportsConflict()
    {
        using var fixture = await WorkspaceFixture.CreateAsync();
        await fixture.CloneAsync("alpha");
        var first = (await fixture.ExecuteAsync("feature", new() { Name = "first", Repositories = ["alpha"] }))
            .Features[0]
            .Name;
        var second = (await fixture.ExecuteAsync("feature", new() { Name = "second", Repositories = ["alpha"] }))
            .Features[1]
            .Name;
        foreach (var feature in new[] { first, second })
        {
            var tree = fixture.Worktree(feature, "alpha");
            await File.WriteAllTextAsync(Path.Combine(tree, "README.md"), feature, Token);
            await WorkspaceFixture.GitAsync(tree, "commit", "-am", feature);
        }
        await fixture.ExecuteAsync("merge", new() { Name = "alpha", Feature = first });
        var failure = await Assert.ThrowsAsync<AgwException>(() =>
            fixture.ExecuteAsync("merge", new() { Name = "alpha", Feature = second })
        );
        Assert.Contains("Git merge failed", failure.Message);
        Assert.True(File.Exists(Path.Combine(fixture.Root, "alpha", ".git", "MERGE_HEAD")));
        Assert.Contains(
            "<<<<<<<",
            await File.ReadAllTextAsync(Path.Combine(fixture.Root, "alpha", "README.md"), Token)
        );
        Assert.True((await fixture.Service.ReadAsync(fixture.Root, Token)).Repositories[0].Dirty);
    }

    [Fact]
    public async Task ExecuteAsync_UnicodeFeatureAndReload_PreservesCatalogAndDefaultBranch()
    {
        using var fixture = await WorkspaceFixture.CreateAsync();
        await fixture.CloneAsync("docs");
        var created = await fixture.ExecuteAsync(
            "feature",
            new()
            {
                Name = "新增扩展",
                Date = new DateOnly(2026, 11, 28),
                Repositories = ["docs"],
            }
        );
        Assert.Equal("261128-新增扩展", Assert.Single(created.Features).Name);
        var service = new RepositoryWorkspace(new FilePathLocks(new InMemoryApplicationLock()), TimeProvider.System);
        var loaded = await service.ReadAsync(fixture.Root, Token);
        Assert.Equal(created.Features[0].Name, Assert.Single(loaded.Features).Name);
        Assert.Equal("trunk", Assert.Single(loaded.Repositories).Branch);
    }

    [Fact(SkipUnless = nameof(IsUnix), Skip = "Symbolic link creation requires Unix in this test.")]
    public async Task ReadAsync_RepositoryReplacedWithSymlink_RejectsWithoutFollowingLink()
    {
        using var fixture = await WorkspaceFixture.CreateAsync();
        await fixture.CloneAsync("alpha");
        var main = Path.Combine(fixture.Root, "alpha");
        var moved = Path.Combine(fixture.Root, "moved");
        Directory.Move(main, moved);
        Directory.CreateSymbolicLink(main, moved);
        var exception = await Assert.ThrowsAsync<AgwException>(() => fixture.Service.ReadAsync(fixture.Root, Token));
        Assert.Contains("symbolic links", exception.Message);
        Assert.Equal("trunk", await WorkspaceFixture.GitAsync(moved, "branch", "--show-current"));
    }

    [Fact(SkipUnless = nameof(IsUnix), Skip = "Symbolic link creation requires Unix in this test.")]
    public async Task ExecuteAsync_DanglingIgnoreSymlink_RejectsBeforeWritingOutsideWorkspace()
    {
        using var fixture = await WorkspaceFixture.CreateAsync();
        var ignore = Path.Combine(fixture.Root, ".gitignore");
        File.Delete(ignore);
        var outside = Path.Combine(Path.GetDirectoryName(fixture.Root)!, "outside-ignore");
        File.CreateSymbolicLink(ignore, outside);
        await Assert.ThrowsAsync<AgwException>(() => fixture.CloneAsync("alpha"));
        Assert.False(File.Exists(outside));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "alpha")));
    }

    private sealed class WorkspaceFixture : IDisposable
    {
        private readonly string _directory = TestDirectoryHelper.Create("repository-workspaces");
        private Process? _daemon;
        public string Root => Path.Combine(_directory, "project");
        public string Url { get; private set; } = "";
        public RepositoryWorkspace Service { get; } =
            new(
                new FilePathLocks(new InMemoryApplicationLock()),
                new TestTimeProvider(new DateTimeOffset(2026, 11, 27, 12, 0, 0, TimeSpan.Zero))
            );

        public static async Task<WorkspaceFixture> CreateAsync()
        {
            var fixture = new WorkspaceFixture();
            try
            {
                Directory.CreateDirectory(fixture.Root);
                await File.WriteAllTextAsync(Path.Combine(fixture.Root, ".gitignore"), "existing-rule\n", Token);
                var seed = Directory.CreateDirectory(Path.Combine(fixture._directory, "seed")).FullName;
                await GitAsync(seed, "init", "-b", "trunk");
                await File.WriteAllTextAsync(Path.Combine(seed, "README.md"), "base\n", Token);
                await GitAsync(seed, "add", ".");
                await GitAsync(seed, "commit", "-m", "Initial");
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                listener.Stop();
                var start = new ProcessStartInfo("git")
                {
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                };
                foreach (
                    var argument in new[]
                    {
                        "daemon",
                        "--reuseaddr",
                        "--export-all",
                        "--listen=127.0.0.1",
                        $"--port={port}",
                        $"--base-path={fixture._directory}",
                        fixture._directory,
                    }
                )
                    start.ArgumentList.Add(argument);
                fixture._daemon = Process.Start(start)!;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                while (true)
                {
                    try
                    {
                        using var client = new TcpClient();
                        await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
                        break;
                    }
                    catch (SocketException)
                    {
                        await Task.Delay(25, timeout.Token);
                    }
                }
                fixture.Url = $"git://127.0.0.1:{port}/seed";
                return fixture;
            }
            catch
            {
                fixture.Dispose();
                throw;
            }
        }

        public Task<RepositoryWorkspaceState> CloneAsync(string name) =>
            ExecuteAsync("clone", new() { Name = name, Url = Url });

        public async Task<RepositoryWorkspaceState> ExecuteAsync(string operation, RepositoryWorkspaceRequest request)
        {
            var result = await Service.ExecuteAsync(Root, operation, request, Token);
            if (operation == "clone")
            {
                await GitAsync(Path.Combine(Root, request.Name), "config", "user.name", "Agw Tests");
                await GitAsync(Path.Combine(Root, request.Name), "config", "user.email", "tests@agw.local");
            }
            return result;
        }

        public string Worktree(string feature, string name) => Path.Combine(Root, ".worktrees", feature, name);

        public static async Task<string> GitAsync(string directory, params string[] arguments)
        {
            var result = await Cli.Wrap("git")
                .WithWorkingDirectory(directory)
                .WithArguments(
                    new[]
                    {
                        "-c",
                        "user.name=Agw Tests",
                        "-c",
                        "user.email=tests@agw.local",
                        "-c",
                        "commit.gpgsign=false",
                    }.Concat(arguments)
                )
                .ExecuteBufferedAsync(Token);
            return result.StandardOutput.Trim();
        }

        public void Dispose()
        {
            if (_daemon is { HasExited: false })
            {
                _daemon.Kill(true);
                _daemon.WaitForExit();
            }
            _daemon?.Dispose();
            TestDirectoryHelper.Delete(_directory);
        }
    }
}
