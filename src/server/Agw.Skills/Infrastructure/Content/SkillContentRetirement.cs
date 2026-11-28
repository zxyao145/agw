using Agw.Shared.Contracts.Coordination;
using Agw.Shared.Runtime;
using Agw.Skills.Application.Content;
using Agw.Skills.Contracts;
using Microsoft.Extensions.Logging;

namespace Agw.Skills.Infrastructure.Content;

public sealed class SkillContentRetirement : ISkillContentRetirement
{
    private const string LockResourcePrefix = "skill-content:";

    private readonly AgwDataPaths _paths;
    private readonly IApplicationLock _applicationLock;
    private readonly ILogger<SkillContentRetirement> _logger;

    public SkillContentRetirement(
        AgwDataPaths paths,
        IApplicationLock applicationLock,
        ILogger<SkillContentRetirement> logger
    )
    {
        _paths = paths;
        _applicationLock = applicationLock;
        _logger = logger;
    }

    public async Task<ILocalSkillDirectoryLease> AcquireAsync(
        SkillContentLocation location,
        CancellationToken cancellationToken
    )
    {
        var lease = await _applicationLock
            .AcquireSharedAsync(GetLockResourceName(location), cancellationToken)
            .ConfigureAwait(false);
        return new ContentLease(this, location, lease);
    }

    public async Task RetireAsync(SkillContentLocation location, CancellationToken cancellationToken)
    {
        var markerPath = GetMarkerPath(location);
        if (Directory.Exists(location.Directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using (
                    File.OpenHandle(
                        markerPath,
                        FileMode.OpenOrCreate,
                        FileAccess.Write,
                        FileShare.ReadWrite | FileShare.Delete
                    )
                ) { }
                _logger.LogInformation("Retired skill content {ContentPath}", location.ContentPath);
            }
            catch (DirectoryNotFoundException) when (!Directory.Exists(location.Directory) && !File.Exists(markerPath))
            {
                return;
            }
        }
        else if (!File.Exists(markerPath))
        {
            return;
        }

        await TryDeleteRetiredAsync(location, cancellationToken).ConfigureAwait(false);
    }

    public async Task SweepAsync(CancellationToken cancellationToken)
    {
        var skillsDirectory = Path.GetFullPath(_paths.SkillsDirectory);
        if (!Directory.Exists(skillsDirectory))
        {
            return;
        }

        var markerOptions = new EnumerationOptions
        {
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = false,
        };
        var markerPaths = Directory
            .EnumerateFiles(skillsDirectory, $"*{SkillContentPaths.RetiredMarkerSuffix}", markerOptions)
            .ToList();
        var contentDirectories = Directory.EnumerateDirectories(skillsDirectory, "*", markerOptions).ToArray();
        foreach (var directory in contentDirectories)
        {
            var directoryName = Path.GetFileName(directory);
            if (File.Exists(Path.Combine(directory, "SKILL.md")))
            {
                continue;
            }

            if (SkillContentPaths.IsOwnerDirectoryName(directoryName))
            {
                AddRetiredMarkers(markerPaths, directory, markerOptions);
                continue;
            }

            if (IsLegacyVersionDirectoryName(directoryName))
            {
                AddRetiredMarkers(markerPaths, directory, markerOptions);
                continue;
            }

            if (!TryGetRollingSkillName(directoryName, out var skillName))
            {
                continue;
            }

            AddRetiredMarkers(markerPaths, directory, markerOptions, skillName + SkillContentPaths.RetiredMarkerSuffix);
        }

        foreach (var markerPath in markerPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = markerPath[..^SkillContentPaths.RetiredMarkerSuffix.Length];
            var contentPath = Path.GetRelativePath(_paths.Root, directory).Replace(Path.DirectorySeparatorChar, '/');
            await TryDeleteRetiredAsync(new SkillContentLocation(contentPath, directory), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static bool IsLegacyVersionDirectoryName(string directoryName)
    {
        if (
            directoryName.Length != 65
            || directoryName[32] != '-'
            || !Guid.TryParseExact(directoryName[..32], "N", out var skillId)
            || !Guid.TryParseExact(directoryName[33..], "N", out var versionId)
        )
        {
            return false;
        }

        return string.Equals(directoryName, $"{skillId:N}-{versionId:N}", StringComparison.Ordinal);
    }

    private static bool TryGetRollingSkillName(string directoryName, out string skillName)
    {
        var separator = directoryName.LastIndexOf('-');
        if (separator < 1 || separator != directoryName.Length - 33)
        {
            skillName = string.Empty;
            return false;
        }

        var versionIdText = directoryName[(separator + 1)..];
        if (
            !Guid.TryParseExact(versionIdText, "N", out var versionId)
            || versionId.Version != 7
            || !string.Equals(versionIdText, versionId.ToString("N"), StringComparison.Ordinal)
        )
        {
            skillName = string.Empty;
            return false;
        }

        skillName = directoryName[..separator];
        return true;
    }

    private static void AddRetiredMarkers(
        List<string> markerPaths,
        string directory,
        EnumerationOptions markerOptions,
        string? expectedFileName = null
    )
    {
        try
        {
            markerPaths.AddRange(
                Directory
                    .EnumerateFiles(directory, $"*{SkillContentPaths.RetiredMarkerSuffix}", markerOptions)
                    .Where(markerPath =>
                        expectedFileName == null
                        || string.Equals(Path.GetFileName(markerPath), expectedFileName, StringComparison.Ordinal)
                    )
            );
        }
        catch (DirectoryNotFoundException) when (!Directory.Exists(directory))
        {
            return;
        }
    }

    private async Task TryDeleteRetiredAsync(SkillContentLocation location, CancellationToken cancellationToken)
    {
        var markerPath = GetMarkerPath(location);
        if (!File.Exists(markerPath))
        {
            return;
        }

        var lease = await _applicationLock
            .TryAcquireExclusiveAsync(GetLockResourceName(location), cancellationToken)
            .ConfigureAwait(false);
        if (lease == null)
        {
            return;
        }

        await using var exclusiveLease = lease;
        if (!File.Exists(markerPath))
        {
            return;
        }

        if (Directory.Exists(location.Directory))
        {
            Directory.Delete(location.Directory, recursive: true);
        }

        File.Delete(markerPath);
        DeleteEmptyVersionDirectory(location.Directory);
        _logger.LogInformation("Deleted retired skill content {ContentPath}", location.ContentPath);
    }

    private void DeleteEmptyVersionDirectory(string directory)
    {
        var parent = Directory.GetParent(directory);
        var skillsDirectory = Path.GetFullPath(_paths.SkillsDirectory);
        if (
            parent?.Parent?.FullName == skillsDirectory
            && !SkillContentPaths.IsOwnerDirectoryName(parent.Name)
            && parent.Exists
            && !parent.EnumerateFileSystemInfos().Any()
        )
        {
            parent.Delete();
        }
    }

    private static string GetMarkerPath(SkillContentLocation location) =>
        location.Directory + SkillContentPaths.RetiredMarkerSuffix;

    private static string GetLockResourceName(SkillContentLocation location) =>
        LockResourcePrefix + location.ContentPath.Replace('\\', '/');

    private sealed class ContentLease : ILocalSkillDirectoryLease
    {
        private readonly SkillContentRetirement _owner;
        private readonly SkillContentLocation _location;
        private IApplicationLockLease? _lease;

        public ContentLease(SkillContentRetirement owner, SkillContentLocation location, IApplicationLockLease lease)
        {
            _owner = owner;
            _location = location;
            _lease = lease;
        }

        public string Path => _location.Directory;

        public async ValueTask DisposeAsync()
        {
            var lease = Interlocked.Exchange(ref _lease, null);
            if (lease == null)
            {
                return;
            }

            await lease.DisposeAsync().ConfigureAwait(false);
            await _owner.TryDeleteRetiredAsync(_location, CancellationToken.None).ConfigureAwait(false);
        }
    }
}
