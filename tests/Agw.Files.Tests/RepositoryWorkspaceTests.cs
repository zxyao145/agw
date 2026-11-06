using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
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
    private const string Feature = "261127-add-extensions";

    public static bool IsUnix => !OperatingSystem.IsWindows();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CreateFeatureAsync_TwoRepositories_CreatesWorktreesSwitchesAndMergesWithTwoParents()
    {
        using var fixture = await WorkspaceFixture.CreateAsync();
        await fixture.CloneAsync("alpha");
        await fixture.CloneAsync("beta");
        await fixture.Service.CreateFeatureAsync(fixture.Root, Feature, ["alpha", "beta"], false, Token);
        var state = await fixture.ReadAsync();
        Assert.Equal(2, state.Repositories.Length);
        Assert.All(state.Repositories, repo => Assert.Equal("trunk", repo.DefaultBranch));
        var feature = Assert.Single(state.Features);
        Assert.Equal(Feature, feature.Name);
        Assert.All(feature.Repositories, repo => Assert.Equal(Feature, repo.Branch));
        var worktree = fixture.Worktree(Feature, "alpha");
        await File.WriteAllTextAsync(Path.Combine(worktree, "feature.txt"), "feature content", Token);
        await WorkspaceFixture.GitAsync(worktree, "add", ".");
        await WorkspaceFixture.GitAsync(worktree, "commit", "-m", "Add feature");
        await fixture.Service.SwitchBranchAsync(fixture.Root, Feature, "alpha", "other-feature", true, Token);
        state = await fixture.ReadAsync();
        Assert.Equal("other-feature", state.Features[0].Repositories[0].Branch);
        Assert.Equal(1, state.Features[0].Repositories[0].Ahead);
        await fixture.Service.MergeAsync(fixture.Root, Feature, "alpha", Token);
        var main = Path.Combine(fixture.Root, "alpha");
        Assert.Equal("feature content", await File.ReadAllTextAsync(Path.Combine(main, "feature.txt"), Token));
        var parents = await WorkspaceFixture.GitAsync(main, "show", "-s", "--format=%P", "HEAD");
        Assert.Equal(2, parents.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Equal("trunk", await WorkspaceFixture.GitAsync(main, "branch", "--show-current"));
        Assert.False(File.Exists(Path.Combine(fixture.Root, "beta", "feature.txt")));
        var ignore = await File.ReadAllTextAsync(Path.Combine(fixture.Root, ".gitignore"), Token);
        Assert.Contains("existing-rule", ignore);
        Assert.Contains("/.worktrees/", ignore);
        Assert.DoesNotContain(".agw", ignore);
        await fixture.CloneAsync("docs");
        await fixture.Service.CreateFeatureAsync(fixture.Root, Feature, ["docs"], true, Token);
        Assert.Equal(3, (await fixture.ReadAsync()).Features[0].Repositories.Length);
    }

    [Fact]
    public async Task ReadAsync_ManualLayout_DiscoversRepositoriesAndReportsUnmanagedWorktrees()
    {
        using var fixture = await WorkspaceFixture.CreateAsync();
        await WorkspaceFixture.GitAsync(fixture.Root, "clone", "--", fixture.Url, "manual");
        Directory.CreateDirectory(Path.Combine(fixture.Root, "notes"));
        Directory.CreateDirectory(Path.Combine(fixture.Root, ".worktrees", Feature, "missing"));
        var state = await fixture.ReadAsync();
        var repo = Assert.Single(state.Repositories);
        Assert.Equal("manual", repo.Name);
        Assert.Equal("trunk", repo.DefaultBranch);
        Assert.Equal(0, repo.Behind);
        Assert.Null(repo.Error);
        var entry = Assert.Single(Assert.Single(state.Features).Repositories);
        Assert.Equal("missing", entry.Name);
        Assert.NotNull(entry.Error);
    }

    [Fact]
    public async Task CloneAsync_NameOmitted_DerivesNameFromUrl()
    {
        using var fixture = await WorkspaceFixture.CreateAsync();
        await WorkspaceFixture.GitAsync(fixture.Seed, "clone", "--bare", "--", ".", "../module-alpha.git");
        await fixture.Service.CloneAsync(fixture.Root, fixture.Url.Replace("/seed", "/module-alpha.git"), null, Token);
        Assert.Equal("module-alpha", Assert.Single((await fixture.ReadAsync()).Repositories).Name);
    }

    [Fact]
    public async Task CloneAsync_CanceledDuringTransfer_RemovesPartialDirectoryAndAllowsRetry()
    {
        using var fixture = await WorkspaceFixture.CreateAsync();
        const int transferBytes = 16 * 1024 * 1024;
        await File.WriteAllBytesAsync(
            Path.Combine(fixture.Seed, "large.bin"),
            RandomNumberGenerator.GetBytes(transferBytes),
            Token
        );
        await WorkspaceFixture.GitAsync(fixture.Seed, "add", ".");
        await WorkspaceFixture.GitAsync(fixture.Seed, "commit", "-m", "Add transfer content");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var destination = Path.Combine(fixture.Root, "alpha");
        var clone = fixture.Service.CloneAsync(fixture.Root, fixture.Url, "alpha", cancellation.Token);
        try
        {
            while (!Directory.Exists(Path.Combine(destination, ".git")))
            {
                Assert.False(clone.IsCompleted);
                await Task.Delay(10, timeout.Token);
            }
            Assert.False(clone.IsCompleted);
        }
        finally
        {
            await cancellation.CancelAsync();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => clone.WaitAsync(timeout.Token));
        Assert.False(Directory.Exists(destination));
        await fixture.CloneAsync("alpha");
        Assert.Equal("alpha", Assert.Single((await fixture.ReadAsync()).Repositories).Name);
    }

    [Fact]
    public async Task ReadAsync_RootOffDefaultBranch_ReportsStateAndRestoreSwitchesBack()
    {
        using var fixture = await WorkspaceFixture.CreateAsync();
        await fixture.CloneAsync("alpha");
        await fixture.Service.CreateFeatureAsync(fixture.Root, Feature, ["alpha"], false, Token);
        var main = Path.Combine(fixture.Root, "alpha");
        await WorkspaceFixture.GitAsync(main, "switch", "-c", "outside-change");
        var repo = Assert.Single((await fixture.ReadAsync()).Repositories);
        Assert.Equal("outside-change", repo.Branch);
        Assert.Equal("trunk", repo.DefaultBranch);
        var failure = await Assert.ThrowsAsync<AgwException>(() =>
            fixture.Service.MergeAsync(fixture.Root, Feature, "alpha", Token)
        );
        Assert.Equal(ErrorCodes.RepositoryStateConflict.Code, failure.Code);
        await Assert.ThrowsAsync<AgwException>(() => fixture.Service.SyncAsync(fixture.Root, "alpha", Token));
        await fixture.Service.RestoreDefaultBranchAsync(fixture.Root, "alpha", Token);
        Assert.Equal("trunk", await WorkspaceFixture.GitAsync(main, "branch", "--show-current"));
    }

    [Fact]
    public async Task SwitchBranchAsync_DirtyWorktree_RejectsSwitchAndMergeWithoutLosingChanges()
    {
        using var fixture = await WorkspaceFixture.CreateAsync();
        await fixture.CloneAsync("alpha");
        await fixture.Service.CreateFeatureAsync(fixture.Root, Feature, ["alpha"], false, Token);
        var worktree = fixture.Worktree(Feature, "alpha");
        await File.WriteAllTextAsync(Path.Combine(worktree, "local.txt"), "uncommitted", Token);
        var failure = await Assert.ThrowsAsync<AgwException>(() =>
            fixture.Service.SwitchBranchAsync(fixture.Root, Feature, "alpha", "new", true, Token)
        );
        Assert.Equal(ErrorCodes.RepositoryStateConflict.Code, failure.Code);
        await Assert.ThrowsAsync<AgwException>(() => fixture.Service.MergeAsync(fixture.Root, Feature, "alpha", Token));
        Assert.Equal("uncommitted", await File.ReadAllTextAsync(Path.Combine(worktree, "local.txt"), Token));
        Assert.True(Assert.Single(Assert.Single((await fixture.ReadAsync()).Features).Repositories).Dirty);
    }

    [Fact]
    public async Task DeleteFeatureAsync_TwoRepositories_RemovesDirectoriesAndRegistrationsAndPreservesBranches()
    {
        using var fixture = await WorkspaceFixture.CreateAsync();
        await fixture.CloneAsync("alpha");
        await fixture.CloneAsync("beta");
        await fixture.Service.CreateFeatureAsync(fixture.Root, Feature, ["alpha", "beta"], false, Token);
        const string otherFeature = "261127-other";
        await fixture.Service.CreateFeatureAsync(fixture.Root, otherFeature, ["alpha"], false, Token);
        var worktree = fixture.Worktree(Feature, "alpha");
        await File.WriteAllTextAsync(Path.Combine(worktree, "feature.txt"), "committed content", Token);
        await WorkspaceFixture.GitAsync(worktree, "add", ".");
        await WorkspaceFixture.GitAsync(worktree, "commit", "-m", "Feature content");
        var commit = await WorkspaceFixture.GitAsync(worktree, "rev-parse", "HEAD");
        var registrations = new List<string>();
        foreach (var name in new[] { "alpha", "beta" })
            registrations.Add(
                await WorkspaceFixture.GitAsync(fixture.Worktree(Feature, name), "rev-parse", "--absolute-git-dir")
            );

        await fixture.Service.DeleteFeatureAsync(fixture.Root, Feature, Token);

        Assert.False(Directory.Exists(Path.Combine(fixture.Root, ".worktrees", Feature)));
        Assert.All(registrations, path => Assert.False(Directory.Exists(path)));
        var state = await fixture.ReadAsync();
        Assert.Equal(otherFeature, Assert.Single(state.Features).Name);
        Assert.True(Directory.Exists(fixture.Worktree(otherFeature, "alpha")));
        Assert.Equal(
            commit,
            await WorkspaceFixture.GitAsync(Path.Combine(fixture.Root, "alpha"), "rev-parse", Feature)
        );
        foreach (var name in new[] { "alpha", "beta" })
        {
            var main = Path.Combine(fixture.Root, name);
            Assert.Contains(Feature, state.Repositories.Single(repo => repo.Name == name).Branches);
            Assert.DoesNotContain(
                $"branch refs/heads/{Feature}\n",
                await WorkspaceFixture.GitAsync(main, "worktree", "list", "--porcelain")
            );
            Assert.Equal("trunk", await WorkspaceFixture.GitAsync(main, "branch", "--show-current"));
        }
    }

    [Theory]
    [InlineData("README.md")]
    [InlineData("untracked.txt")]
    public async Task DeleteFeatureAsync_OneDirtyWorktree_RejectsBeforeRemovingAnyWorktree(string fileName)
    {
        using var fixture = await WorkspaceFixture.CreateAsync();
        await fixture.CloneAsync("alpha");
        await fixture.CloneAsync("beta");
        await fixture.Service.CreateFeatureAsync(fixture.Root, Feature, ["alpha", "beta"], false, Token);
        var worktree = fixture.Worktree(Feature, "beta");
        await WorkspaceFixture.GitAsync(worktree, "config", "status.showUntrackedFiles", "no");
        await File.WriteAllTextAsync(Path.Combine(worktree, fileName), "uncommitted content", Token);

        var failure = await Assert.ThrowsAsync<AgwException>(() =>
            fixture.Service.DeleteFeatureAsync(fixture.Root, Feature, Token)
        );

        Assert.Equal(ErrorCodes.RepositoryStateConflict.Code, failure.Code);
        Assert.True(Directory.Exists(fixture.Worktree(Feature, "alpha")));
        Assert.Equal("uncommitted content", await File.ReadAllTextAsync(Path.Combine(worktree, fileName), Token));
        Assert.Equal(2, Assert.Single((await fixture.ReadAsync()).Features).Repositories.Length);
    }

    [Fact]
    public async Task DeleteFeatureAsync_OneLockedWorktree_RejectsBeforeRemovingAnyWorktree()
    {
        using var fixture = await WorkspaceFixture.CreateAsync();
        await fixture.CloneAsync("alpha");
        await fixture.CloneAsync("beta");
        await fixture.Service.CreateFeatureAsync(fixture.Root, Feature, ["alpha", "beta"], false, Token);
        var main = Path.Combine(fixture.Root, "beta");
        var worktree = fixture.Worktree(Feature, "beta");
        await WorkspaceFixture.GitAsync(main, "worktree", "lock", "--reason", "Keep worktree", "--", worktree);

        var failure = await Assert.ThrowsAsync<AgwException>(() =>
            fixture.Service.DeleteFeatureAsync(fixture.Root, Feature, Token)
        );

        Assert.Equal(ErrorCodes.RepositoryStateConflict.Code, failure.Code);
        Assert.True(Directory.Exists(fixture.Worktree(Feature, "alpha")));
        Assert.True(Directory.Exists(worktree));
        await WorkspaceFixture.GitAsync(main, "worktree", "unlock", "--", worktree);
        await fixture.Service.DeleteFeatureAsync(fixture.Root, Feature, Token);
        Assert.Empty((await fixture.ReadAsync()).Features);
    }

    [Fact]
    public async Task DeleteFeatureAsync_FileOutsideWorktrees_RejectsBeforeRemovingAnyWorktree()
    {
        using var fixture = await WorkspaceFixture.CreateAsync();
        await fixture.CloneAsync("alpha");
        await fixture.Service.CreateFeatureAsync(fixture.Root, Feature, ["alpha"], false, Token);
        var file = Path.Combine(fixture.Root, ".worktrees", Feature, "notes.txt");
        await File.WriteAllTextAsync(file, "feature notes", Token);

        var failure = await Assert.ThrowsAsync<AgwException>(() =>
            fixture.Service.DeleteFeatureAsync(fixture.Root, Feature, Token)
        );

        Assert.Equal(ErrorCodes.RepositoryStateConflict.Code, failure.Code);
        Assert.Equal("feature notes", await File.ReadAllTextAsync(file, Token));
        Assert.True(Directory.Exists(fixture.Worktree(Feature, "alpha")));
    }

    [Fact]
    public async Task DeleteFeatureAsync_EmptyFeature_RemovesFeatureDirectory()
    {
        using var fixture = await WorkspaceFixture.CreateAsync();
        var path = Path.Combine(fixture.Root, ".worktrees", Feature);
        Directory.CreateDirectory(path);

        await fixture.Service.DeleteFeatureAsync(fixture.Root, Feature, Token);

        Assert.False(Directory.Exists(path));
        Assert.Empty((await fixture.ReadAsync()).Features);
    }

    [Fact]
    public async Task DeleteFeatureAsync_IgnoredArtifacts_RemovesWorktreeDirectory()
    {
        using var fixture = await WorkspaceFixture.CreateAsync();
        await File.WriteAllTextAsync(Path.Combine(fixture.Seed, ".gitignore"), "build/\n", Token);
        await WorkspaceFixture.GitAsync(fixture.Seed, "add", ".gitignore");
        await WorkspaceFixture.GitAsync(fixture.Seed, "commit", "-m", "Ignore build artifacts");
        await fixture.CloneAsync("alpha");
        await fixture.Service.CreateFeatureAsync(fixture.Root, Feature, ["alpha"], false, Token);
        var artifacts = Path.Combine(fixture.Worktree(Feature, "alpha"), "build");
        Directory.CreateDirectory(artifacts);
        await File.WriteAllTextAsync(Path.Combine(artifacts, "generated.txt"), "generated content", Token);

        await fixture.Service.DeleteFeatureAsync(fixture.Root, Feature, Token);

        Assert.False(Directory.Exists(Path.Combine(fixture.Root, ".worktrees", Feature)));
    }

    [Fact]
    public async Task DeleteFeatureAsync_WorktreeFromAnotherRepository_RejectsBeforeRemovingAnyWorktree()
    {
        using var fixture = await WorkspaceFixture.CreateAsync();
        await fixture.CloneAsync("alpha");
        await fixture.CloneAsync("beta");
        await fixture.Service.CreateFeatureAsync(fixture.Root, Feature, ["alpha"], false, Token);
        await WorkspaceFixture.GitAsync(
            Path.Combine(fixture.Root, "alpha"),
            "worktree",
            "add",
            "-b",
            "other-branch",
            "--",
            fixture.Worktree(Feature, "beta")
        );

        var failure = await Assert.ThrowsAsync<AgwException>(() =>
            fixture.Service.DeleteFeatureAsync(fixture.Root, Feature, Token)
        );

        Assert.Equal(ErrorCodes.RepositoryStateConflict.Code, failure.Code);
        Assert.True(Directory.Exists(fixture.Worktree(Feature, "alpha")));
        Assert.True(Directory.Exists(fixture.Worktree(Feature, "beta")));
    }

    [Fact]
    public async Task DeleteFeatureAsync_UninitializedSubmodule_RemovesWorktree()
    {
        using var fixture = await WorkspaceFixture.CreateAsync();
        await WorkspaceFixture.GitAsync(fixture.Seed, "submodule", "add", "--", fixture.Url, "module");
        await WorkspaceFixture.GitAsync(fixture.Seed, "commit", "-am", "Add submodule");
        await fixture.CloneAsync("alpha");
        await fixture.Service.CreateFeatureAsync(fixture.Root, Feature, ["alpha"], false, Token);

        await fixture.Service.DeleteFeatureAsync(fixture.Root, Feature, Token);

        Assert.False(Directory.Exists(fixture.Worktree(Feature, "alpha")));
        Assert.Empty((await fixture.ReadAsync()).Features);
    }

    [Fact]
    public async Task DeleteFeatureAsync_InitializedSubmodule_RejectsBeforeRemovingAnyWorktree()
    {
        using var fixture = await WorkspaceFixture.CreateAsync();
        await WorkspaceFixture.GitAsync(fixture.Seed, "submodule", "add", "--", fixture.Url, "module");
        await WorkspaceFixture.GitAsync(fixture.Seed, "commit", "-am", "Add submodule");
        await fixture.CloneAsync("alpha");
        await fixture.CloneAsync("beta");
        await fixture.Service.CreateFeatureAsync(fixture.Root, Feature, ["alpha", "beta"], false, Token);
        var worktree = fixture.Worktree(Feature, "beta");
        await WorkspaceFixture.GitAsync(worktree, "submodule", "update", "--init");

        var failure = await Assert.ThrowsAsync<AgwException>(() =>
            fixture.Service.DeleteFeatureAsync(fixture.Root, Feature, Token)
        );

        Assert.Equal(ErrorCodes.RepositoryStateConflict.Code, failure.Code);
        Assert.Contains("Deinitialize submodules", failure.Message);
        Assert.True(Directory.Exists(fixture.Worktree(Feature, "alpha")));
        Assert.True(File.Exists(Path.Combine(worktree, "module", "README.md")));
    }

    [Theory]
    [InlineData("missing", 404_0007)]
    [InlineData("../alpha", 400_0001)]
    public async Task DeleteFeatureAsync_InvalidFeature_RejectsWithoutChangingRepositories(string feature, int code)
    {
        using var fixture = await WorkspaceFixture.CreateAsync();
        await fixture.CloneAsync("alpha");

        var failure = await Assert.ThrowsAsync<AgwException>(() =>
            fixture.Service.DeleteFeatureAsync(fixture.Root, feature, Token)
        );

        Assert.Equal(code, failure.Code);
        Assert.True(File.Exists(Path.Combine(fixture.Root, "alpha", "README.md")));
    }

    [Fact(SkipUnless = nameof(IsUnix), Skip = "Symbolic link creation requires Unix in this test.")]
    public async Task DeleteFeatureAsync_FeatureSymlink_RejectsWithoutRemovingTarget()
    {
        using var fixture = await WorkspaceFixture.CreateAsync();
        await fixture.CloneAsync("alpha");
        await fixture.Service.CreateFeatureAsync(fixture.Root, Feature, ["alpha"], false, Token);
        var path = Path.Combine(fixture.Root, ".worktrees", Feature);
        var moved = Path.Combine(Path.GetDirectoryName(fixture.Root)!, "outside-feature");
        Directory.Move(path, moved);
        Directory.CreateSymbolicLink(path, moved);

        var failure = await Assert.ThrowsAsync<AgwException>(() =>
            fixture.Service.DeleteFeatureAsync(fixture.Root, Feature, Token)
        );

        Assert.Equal(ErrorCodes.InvalidParam.Code, failure.Code);
        Assert.True(File.Exists(Path.Combine(moved, "alpha", "README.md")));
    }

    [Fact]
    public async Task CreateFeatureAsync_InvalidOrDuplicateTargets_DoesNotCreatePartialFeature()
    {
        using var fixture = await WorkspaceFixture.CreateAsync();
        await fixture.CloneAsync("alpha");
        await Assert.ThrowsAsync<AgwException>(() =>
            fixture.Service.CloneAsync(fixture.Root, fixture.Url, "../escape", Token)
        );
        await Assert.ThrowsAsync<AgwException>(() =>
            fixture.Service.CloneAsync(fixture.Root, fixture.Root, "local", Token)
        );
        await Assert.ThrowsAsync<AgwException>(() => fixture.CloneAsync("alpha"));
        var missing = await Assert.ThrowsAsync<AgwException>(() =>
            fixture.Service.CreateFeatureAsync(fixture.Root, Feature, ["alpha", "missing"], false, Token)
        );
        Assert.Equal(ErrorCodes.ResourceNotFound.Code, missing.Code);
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, ".worktrees")));
        var unknown = await Assert.ThrowsAsync<AgwException>(() =>
            fixture.Service.CreateFeatureAsync(fixture.Root, Feature, ["alpha"], true, Token)
        );
        Assert.Equal(ErrorCodes.ResourceNotFound.Code, unknown.Code);
        await fixture.Service.CreateFeatureAsync(fixture.Root, Feature, ["alpha"], false, Token);
        var duplicate = await Assert.ThrowsAsync<AgwException>(() =>
            fixture.Service.CreateFeatureAsync(fixture.Root, Feature, ["alpha"], false, Token)
        );
        Assert.Equal(ErrorCodes.RepositoryStateConflict.Code, duplicate.Code);
        Assert.Equal(
            "trunk",
            await WorkspaceFixture.GitAsync(Path.Combine(fixture.Root, "alpha"), "branch", "--show-current")
        );
    }

    [Fact]
    public async Task MergeAsync_Conflict_AbortsAndKeepsRootCheckoutClean()
    {
        using var fixture = await WorkspaceFixture.CreateAsync();
        await fixture.CloneAsync("alpha");
        foreach (var feature in new[] { "261127-first", "261127-second" })
        {
            await fixture.Service.CreateFeatureAsync(fixture.Root, feature, ["alpha"], false, Token);
            var tree = fixture.Worktree(feature, "alpha");
            await File.WriteAllTextAsync(Path.Combine(tree, "README.md"), feature, Token);
            await WorkspaceFixture.GitAsync(tree, "commit", "-am", feature);
        }
        await fixture.Service.MergeAsync(fixture.Root, "261127-first", "alpha", Token);
        var failure = await Assert.ThrowsAsync<AgwException>(() =>
            fixture.Service.MergeAsync(fixture.Root, "261127-second", "alpha", Token)
        );
        Assert.Equal(ErrorCodes.RepositoryMergeConflict.Code, failure.Code);
        Assert.Contains("README.md", failure.Message);
        var main = Path.Combine(fixture.Root, "alpha");
        Assert.False(File.Exists(Path.Combine(main, ".git", "MERGE_HEAD")));
        Assert.Equal("261127-first", await File.ReadAllTextAsync(Path.Combine(main, "README.md"), Token));
        Assert.False(Assert.Single((await fixture.ReadAsync()).Repositories).Dirty);
    }

    [Fact]
    public async Task SyncAsync_RemoteAdvanced_FastForwardsDefaultBranch()
    {
        using var fixture = await WorkspaceFixture.CreateAsync();
        await fixture.CloneAsync("alpha");
        await File.WriteAllTextAsync(Path.Combine(fixture.Seed, "README.md"), "remote change\n", Token);
        await WorkspaceFixture.GitAsync(fixture.Seed, "commit", "-am", "Remote change");
        await fixture.Service.SyncAsync(fixture.Root, "alpha", Token);
        Assert.Contains(
            "remote change",
            await File.ReadAllTextAsync(Path.Combine(fixture.Root, "alpha", "README.md"), Token)
        );
        var repo = Assert.Single((await fixture.ReadAsync()).Repositories);
        Assert.Equal(0, repo.Behind);
        Assert.Equal(0, repo.Ahead);
    }

    [Fact]
    public async Task SyncAsync_HttpProxyConfigured_TransfersThroughProxyWithoutPersistingGitSettings()
    {
        using var fixture = await WorkspaceFixture.CreateAsync();
        await fixture.CloneAsync("alpha");
        await WorkspaceFixture.GitAsync(fixture.Seed, "update-server-info");
        await using var proxy = await RepositoryProxyServer.StartAsync(fixture.Seed, Token);
        await WorkspaceFixture.GitAsync(
            Path.Combine(fixture.Root, "alpha"),
            "remote",
            "set-url",
            "origin",
            RepositoryProxyServer.RepositoryUrl
        );
        await File.WriteAllTextAsync(Path.Combine(fixture.Seed, "README.md"), "proxied change\n", Token);
        await WorkspaceFixture.GitAsync(fixture.Seed, "commit", "-am", "Remote change");
        await WorkspaceFixture.GitAsync(fixture.Seed, "update-server-info");
        await fixture.Service.SyncAsync(fixture.Root, "alpha", Token, proxy.ProxyUrl);

        Assert.True(proxy.Requests > 0);
        Assert.Equal(
            "proxied change",
            (await File.ReadAllTextAsync(Path.Combine(fixture.Root, "alpha", "README.md"), Token)).TrimEnd()
        );
        Assert.DoesNotContain(
            "proxy",
            await File.ReadAllTextAsync(Path.Combine(fixture.Root, "alpha", ".git", "config"), Token)
        );
    }

    [Theory]
    [InlineData("ftp://127.0.0.1:7890")]
    [InlineData("127.0.0.1:7890")]
    [InlineData("http://")]
    public async Task CloneAsync_InvalidProxy_RejectsBeforeCreatingDirectory(string proxyUrl)
    {
        using var fixture = await WorkspaceFixture.CreateAsync();

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            fixture.Service.CloneAsync(fixture.Root, fixture.Url, "alpha", Token, proxyUrl)
        );

        Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code);
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "alpha")));
    }

    [Fact]
    public async Task CreateFeatureAsync_UnicodeName_CreatesBranchAndWorktree()
    {
        using var fixture = await WorkspaceFixture.CreateAsync();
        await fixture.CloneAsync("docs");
        await fixture.Service.CreateFeatureAsync(fixture.Root, "261128-新增扩展", ["docs"], false, Token);
        var feature = Assert.Single((await fixture.ReadAsync()).Features);
        Assert.Equal("261128-新增扩展", feature.Name);
        Assert.Equal("261128-新增扩展", Assert.Single(feature.Repositories).Branch);
    }

    [Fact]
    public async Task ListTreeAsync_CheckedOutAndCommittedBranches_ListsDiskOrCommitEntries()
    {
        using var fixture = await WorkspaceFixture.CreateAsync();
        await fixture.CloneAsync("alpha");
        await fixture.Service.CreateFeatureAsync(fixture.Root, Feature, ["alpha"], false, Token);
        var worktree = fixture.Worktree(Feature, "alpha");
        Directory.CreateDirectory(Path.Combine(worktree, "src"));
        await File.WriteAllTextAsync(Path.Combine(worktree, "src", "feature.txt"), "committed", Token);
        await WorkspaceFixture.GitAsync(worktree, "add", ".");
        await WorkspaceFixture.GitAsync(worktree, "commit", "-m", "Add feature");
        await File.WriteAllTextAsync(Path.Combine(worktree, "notes.txt"), "untracked", Token);

        var checkedOut = await fixture.Service.ListTreeAsync(fixture.Root, "alpha", Feature, "", Token);
        Assert.Equal($".worktrees/{Feature}/alpha", checkedOut.Checkout);
        Assert.Equal(
            [("src", true), ("notes.txt", false), ("README.md", false)],
            checkedOut.Entries.Select(entry => (entry.Name, entry.IsDirectory))
        );
        Assert.Equal(
            "alpha",
            (await fixture.Service.ListTreeAsync(fixture.Root, "alpha", "trunk", "", Token)).Checkout
        );

        File.Delete(Path.Combine(worktree, "notes.txt"));
        await fixture.Service.SwitchBranchAsync(fixture.Root, Feature, "alpha", "side", true, Token);
        var committed = await fixture.Service.ListTreeAsync(fixture.Root, "alpha", Feature, "src", Token);
        Assert.Null(committed.Checkout);
        Assert.Equal("src/feature.txt", Assert.Single(committed.Entries).Path);
        var remote = await fixture.Service.ListTreeAsync(fixture.Root, "alpha", "origin/trunk", "", Token);
        Assert.Null(remote.Checkout);
        Assert.Equal("README.md", Assert.Single(remote.Entries).Name);
        var blob = await fixture.Service.ReadBlobAsync(fixture.Root, "alpha", Feature, "src/feature.txt", Token);
        Assert.Equal("committed", blob.Content);
        Assert.False(blob.IsBinary);

        var missing = await Assert.ThrowsAsync<AgwException>(() =>
            fixture.Service.ReadBlobAsync(fixture.Root, "alpha", Feature, "missing.txt", Token)
        );
        Assert.Equal(ErrorCodes.ResourceNotFound.Code, missing.Code);
        var escape = await Assert.ThrowsAsync<AgwException>(() =>
            fixture.Service.ListTreeAsync(fixture.Root, "alpha", "trunk", "../beta", Token)
        );
        Assert.Equal(ErrorCodes.InvalidParam.Code, escape.Code);
        var unknown = await Assert.ThrowsAsync<AgwException>(() =>
            fixture.Service.ListTreeAsync(fixture.Root, "alpha", "no-such-branch", "", Token)
        );
        Assert.Equal(ErrorCodes.ResourceNotFound.Code, unknown.Code);
    }

    [Fact]
    public async Task ListChangesAsync_CheckedOutBranch_ReportsUncommittedChanges()
    {
        using var fixture = await WorkspaceFixture.CreateAsync();
        await fixture.CloneAsync("alpha");
        await fixture.Service.CreateFeatureAsync(fixture.Root, Feature, ["alpha"], false, Token);
        var worktree = fixture.Worktree(Feature, "alpha");
        await File.WriteAllTextAsync(Path.Combine(worktree, "README.md"), "changed", Token);
        Directory.CreateDirectory(Path.Combine(worktree, "docs"));
        await File.WriteAllTextAsync(Path.Combine(worktree, "docs", "new.md"), "new", Token);
        var changes = await fixture.Service.ListChangesAsync(fixture.Root, "alpha", Feature, Token);
        Assert.Equal([new("README.md", "modified"), new RepositoryChange("docs/new.md", "untracked")], changes);
        Assert.Empty(await fixture.Service.ListChangesAsync(fixture.Root, "alpha", "trunk", Token));
        Assert.Empty(await fixture.Service.ListChangesAsync(fixture.Root, "alpha", "origin/trunk", Token));
        var repo = Assert.Single((await fixture.ReadAsync()).Repositories);
        Assert.Equal(["trunk"], repo.RemoteBranches.Select(name => name["origin/".Length..]));
    }

    [Fact(SkipUnless = nameof(IsUnix), Skip = "Symbolic link creation requires Unix in this test.")]
    public async Task ReadAsync_RepositoryReplacedWithSymlink_IgnoresLinkAndRejectsOperations()
    {
        using var fixture = await WorkspaceFixture.CreateAsync();
        await fixture.CloneAsync("alpha");
        var main = Path.Combine(fixture.Root, "alpha");
        var moved = Path.Combine(fixture.Root, "moved");
        Directory.Move(main, moved);
        Directory.CreateSymbolicLink(main, moved);
        Assert.Equal(["moved"], (await fixture.ReadAsync()).Repositories.Select(repo => repo.Name));
        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            fixture.Service.RestoreDefaultBranchAsync(fixture.Root, "alpha", Token)
        );
        Assert.Contains("symbolic links", exception.Message);
    }

    [Fact(SkipUnless = nameof(IsUnix), Skip = "Symbolic link creation requires Unix in this test.")]
    public async Task CreateFeatureAsync_DanglingIgnoreSymlink_RejectsBeforeWritingOutsideWorkspace()
    {
        using var fixture = await WorkspaceFixture.CreateAsync();
        await fixture.CloneAsync("alpha");
        var ignore = Path.Combine(fixture.Root, ".gitignore");
        File.Delete(ignore);
        var outside = Path.Combine(Path.GetDirectoryName(fixture.Root)!, "outside-ignore");
        File.CreateSymbolicLink(ignore, outside);
        await Assert.ThrowsAsync<AgwException>(() =>
            fixture.Service.CreateFeatureAsync(fixture.Root, Feature, ["alpha"], false, Token)
        );
        Assert.False(File.Exists(outside));
        Assert.False(Directory.Exists(fixture.Worktree(Feature, "alpha")));
    }

    private sealed class WorkspaceFixture : IDisposable
    {
        private readonly string _directory = TestDirectoryHelper.Create("repository-workspaces");
        private Process? _daemon;
        public string Root => Path.Combine(_directory, "project");
        public string Seed => Path.Combine(_directory, "seed");
        public string Url { get; private set; } = "";
        public RepositoryWorkspace Service { get; } = new(new FilePathLocks(new InMemoryApplicationLock()));

        public static async Task<WorkspaceFixture> CreateAsync()
        {
            var fixture = new WorkspaceFixture();
            try
            {
                Directory.CreateDirectory(fixture.Root);
                await File.WriteAllTextAsync(Path.Combine(fixture.Root, ".gitignore"), "existing-rule\n", Token);
                Directory.CreateDirectory(fixture.Seed);
                await GitAsync(fixture.Seed, "init", "-b", "trunk");
                await File.WriteAllTextAsync(Path.Combine(fixture.Seed, "README.md"), "base\n", Token);
                await GitAsync(fixture.Seed, "add", ".");
                await GitAsync(fixture.Seed, "commit", "-m", "Initial");
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

        public async Task CloneAsync(string name)
        {
            await Service.CloneAsync(Root, Url, name, Token);
            await GitAsync(Path.Combine(Root, name), "config", "user.name", "Agw Tests");
            await GitAsync(Path.Combine(Root, name), "config", "user.email", "tests@agw.local");
        }

        public Task<RepositoryWorkspaceState> ReadAsync() => Service.ReadAsync(Root, Token);

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
