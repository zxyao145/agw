using System.IO.Compression;
using System.Runtime.ExceptionServices;
using System.Text;
using Agw.Agents.Contracts.Catalog;
using Agw.Shared.Contracts;
using Agw.Shared.Contracts.Coordination;
using Agw.Shared.Contracts.Pagination;
using Agw.Shared.Coordination;
using Agw.Shared.Data.Entities.Skills;
using Agw.Shared.Data.Pagination;
using Agw.Shared.Exceptions;
using Agw.Shared.Runtime;
using Agw.Skills.Application.Content;
using Agw.Skills.Application.Persistence;
using Agw.Skills.Application.Remote;
using Agw.Skills.Contracts;
using Agw.Skills.Contracts.Registration;
using Agw.Skills.Contracts.Remote;
using Agw.Skills.Domain.Behaviors;
using Agw.Skills.Domain.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agw.Skills.Application;

public sealed record SkillDetails(Skill Skill, IReadOnlyList<Guid> AgentIds, bool IsBuiltIn);

public class SkillAppService
{
    private const string PreparedSkillDirectoryPrefix = "agw-skill-prepared-";

    private readonly ISkillsDbContext _dbContext;
    private readonly IAgentReferenceFacade _agentReferences;
    private readonly SkillNameUniquenessDomainService _nameUniquenessDomainService;
    private readonly AgwDataPaths _dataPaths;
    private readonly ILogger<SkillAppService> _logger;
    private readonly IReadOnlySet<Guid> _builtInSkillIds;
    private readonly IRemoteSkillClient _remoteSkillClient;
    private readonly IRemoteSkillRefreshLock _remoteSkillRefreshLock;
    private readonly IApplicationLock _applicationLock;
    private readonly ISkillContentRetirement _contentRetirement;
    private readonly TimeProvider _timeProvider;
    private readonly ICurrentUser _currentUser;
    private readonly bool _rollingUpdate;

    public SkillAppService(
        ISkillsDbContext dbContext,
        IAgentReferenceFacade agentReferences,
        SkillNameUniquenessDomainService nameUniquenessDomainService,
        AgwDataPaths dataPaths,
        ILogger<SkillAppService> logger,
        IRemoteSkillClient remoteSkillClient,
        IRemoteSkillRefreshLock remoteSkillRefreshLock,
        IApplicationLock applicationLock,
        TimeProvider timeProvider,
        ICurrentUser currentUser,
        ISkillContentRetirement contentRetirement,
        IEnumerable<IAgentSkillRegistration>? skillRegistrations = null,
        IOptions<SkillOptions>? options = null
    )
    {
        _dbContext = dbContext;
        _agentReferences = agentReferences;
        _nameUniquenessDomainService = nameUniquenessDomainService;
        _dataPaths = dataPaths;
        _logger = logger;
        _remoteSkillClient = remoteSkillClient;
        _remoteSkillRefreshLock = remoteSkillRefreshLock;
        _applicationLock = applicationLock;
        _contentRetirement = contentRetirement;
        _timeProvider = timeProvider;
        _currentUser = currentUser;
        _rollingUpdate = options?.Value.RollingUpdate ?? true;
        _builtInSkillIds = (skillRegistrations ?? []).Select(registration => registration.Id).ToHashSet();
    }

    public async Task<IReadOnlyList<SkillDetails>> ListAsync()
    {
        var ownerUserId = ResolveOwnerUserId();
        var skills = await _dbContext
            .Skills.AsNoTracking()
            .Where(skill => skill.Kind == SkillKind.BuiltIn || skill.CreateBy == ownerUserId)
            .ToListAsync();
        return await AttachAgentIdsAsync(skills);
    }

    public async Task<PagedResult<SkillDetails>> ListPageAsync(
        int pageIndex,
        int pageSize,
        CancellationToken cancellationToken = default
    )
    {
        var page = await UpdatedTimePagination.ToPagedResultAsync(
            _dbContext
                .Skills.AsNoTracking()
                .Where(skill => skill.Kind == SkillKind.BuiltIn || skill.CreateBy == ResolveOwnerUserId()),
            skill => skill.Id,
            pageIndex,
            pageSize,
            cancellationToken
        );
        var details = await AttachAgentIdsAsync(page.Items);

        return new PagedResult<SkillDetails>
        {
            Items = details,
            Total = page.Total,
            PageIndex = page.PageIndex,
            PageSize = page.PageSize,
        };
    }

    public async Task<SkillDetails?> GetAsync(Guid id)
    {
        var ownerUserId = ResolveOwnerUserId();
        var skill = await _dbContext
            .Skills.AsNoTracking()
            .FirstOrDefaultAsync(skill =>
                skill.Id == id && (skill.Kind == SkillKind.BuiltIn || skill.CreateBy == ownerUserId)
            );
        if (skill == null)
        {
            return null;
        }

        var agentIds = await GetSkillAgentIdsAsync(skill.Id);
        return new SkillDetails(skill, agentIds, IsBuiltIn(skill));
    }

    public async Task<ILocalSkillDirectoryLease?> AcquireLocalDirectoryAsync(
        Guid skillId,
        CancellationToken cancellationToken = default
    )
    {
        ILocalSkillDirectoryLease? contentLease = null;
        try
        {
            await using var mutation = new ApplicationLockScope(
                await _applicationLock.AcquireAsync(SkillMutationLock.GetResourceName(skillId), cancellationToken),
                cancellationToken
            );
            var ownerUserId = ResolveOwnerUserId();
            var skill = await _dbContext
                .Skills.AsNoTracking()
                .SingleOrDefaultAsync(
                    item => item.Id == skillId && item.Kind == SkillKind.Local && item.CreateBy == ownerUserId,
                    mutation.Token
                );
            if (skill == null)
            {
                return null;
            }

            var location = GetSkillContentLocation(skill);
            if (IsDirectContentDirectory(location.Directory))
            {
                SkillDirectoryReplacement.Recover(location.Directory, skill.Extra.ContentRevision);
            }

            if (!Directory.Exists(location.Directory))
            {
                return null;
            }

            if (Path.GetFileName(location.Directory) != skill.Name)
            {
                location = await NormalizeLocalDirectoryAsync(skill, location.Directory, ownerUserId, mutation.Token);
                if (location == null)
                {
                    return null;
                }
            }

            contentLease = await _contentRetirement.AcquireAsync(location, mutation.Token).ConfigureAwait(false);
        }
        catch
        {
            if (contentLease != null)
            {
                await contentLease.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }

        return contentLease;
    }

    private async Task<SkillContentLocation?> NormalizeLocalDirectoryAsync(
        Skill skillSnapshot,
        string originalDirectory,
        string ownerUserId,
        CancellationToken token
    )
    {
        var trackedSkill = _dbContext.Skills.Local.SingleOrDefault(item => item.Id == skillSnapshot.Id);
        if (trackedSkill != null)
        {
            _dbContext.Skills.Entry(trackedSkill).State = EntityState.Detached;
        }

        var skill = await _dbContext.Skills.SingleOrDefaultAsync(
            item => item.Id == skillSnapshot.Id && item.Kind == SkillKind.Local && item.CreateBy == ownerUserId,
            token
        );
        if (skill == null)
        {
            return null;
        }

        var original = new LocalSkillState(skill.Name, skill.Description, skill.ContentPath, skill.Extra);
        var preparedRoot = Path.Combine(
            _dataPaths.TempDirectory,
            $"{PreparedSkillDirectoryPrefix}{Guid.CreateVersion7():N}"
        );
        await using var prepared = new PreparedSkillDirectory(preparedRoot, Path.Combine(preparedRoot, skill.Name));
        CopyDirectory(originalDirectory, prepared.DirectoryPath);
        var contentPath = GetRollingContentPath(skill);
        PublishDirectory(prepared.DirectoryPath, GetSkillAbsolutePath(contentPath));
        skill.ContentPath = contentPath;
        try
        {
            await _dbContext.SaveChangesAsync(token);
        }
        catch (Exception exception)
        {
            await RestoreLocalSkillAsync(skill, original, originalDirectory, backupPath: null, exception);
            throw;
        }
        await RetireDirectoryIfDifferentAsync(originalDirectory, GetSkillAbsolutePath(skill)).ConfigureAwait(false);
        return GetSkillContentLocation(skill);
    }

    public async Task<SkillDetails> CreateAsync(
        Skill skill,
        IFormFile? archive,
        string user,
        string? remoteUrl = null,
        string? sourceDigest = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(skill);

        skill.RemoteUrl = remoteUrl;
        new SkillBehavior(skill).EnsureCreatable(archive != null, sourceDigest);
        return skill.Kind == SkillKind.Local
            ? await CreateLocalAsync(skill, archive!, sourceDigest, cancellationToken)
            : await CreateRemoteAsync(skill, cancellationToken);
    }

    public async Task<SkillDetails?> UpdateAsync(
        Guid id,
        string name,
        string description,
        IFormFile? archive,
        string user,
        string? remoteUrl = null,
        string? sourceDigest = null,
        CancellationToken cancellationToken = default
    )
    {
        await using var mutation = new ApplicationLockScope(
            await _applicationLock.AcquireAsync(SkillMutationLock.GetResourceName(id), cancellationToken),
            cancellationToken
        );
        cancellationToken = mutation.Token;
        var ownerUserId = ResolveOwnerUserId();
        var existing = await _dbContext.Skills.SingleOrDefaultAsync(
            skill => skill.Id == id && (skill.Kind == SkillKind.BuiltIn || skill.CreateBy == ownerUserId),
            cancellationToken
        );
        if (existing == null)
        {
            return null;
        }

        var behavior = new SkillBehavior(existing);
        behavior.EnsureMutable(_builtInSkillIds.Contains(existing.Id));
        behavior.EnsureUpdatable(name, archive != null, remoteUrl, sourceDigest);
        return existing.Kind == SkillKind.Local
            ? await UpdateLocalAsync(existing, name, description, archive, sourceDigest, cancellationToken)
            : await UpdateRemoteAsync(existing, remoteUrl, cancellationToken);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var mutation = new ApplicationLockScope(
            await _applicationLock.AcquireAsync(SkillMutationLock.GetResourceName(id), cancellationToken),
            cancellationToken
        );
        cancellationToken = mutation.Token;
        var ownerUserId = ResolveOwnerUserId();
        var existing = await _dbContext
            .Skills.AsNoTracking()
            .FirstOrDefaultAsync(
                skill => skill.Id == id && (skill.Kind == SkillKind.BuiltIn || skill.CreateBy == ownerUserId),
                cancellationToken
            );
        if (existing == null)
        {
            return false;
        }

        new SkillBehavior(existing).EnsureMutable(_builtInSkillIds.Contains(existing.Id));
        if (existing.Kind == SkillKind.Remote)
        {
            await using var refreshLease = await _remoteSkillRefreshLock.AcquireAsync(existing.Id, cancellationToken);
            return await DeleteCoreAsync(existing, cancellationToken);
        }

        var directory = GetSkillAbsolutePath(existing);
        if (!IsDirectContentDirectory(directory))
        {
            return await DeleteCoreAsync(existing, cancellationToken);
        }

        await using var ownerMutation = await AcquireOwnerMutationAsync(cancellationToken);
        SkillDirectoryReplacement.Recover(directory, existing.Extra.ContentRevision);
        return await DeleteCoreAsync(existing, ownerMutation.Token);
    }

    private async Task<SkillDetails> CreateLocalAsync(
        Skill skill,
        IFormFile archive,
        string? sourceDigest,
        CancellationToken cancellationToken
    )
    {
        new SkillBehavior(skill).DefineLocal(skill.Name, skill.Description);
        await using var ownerMutation = _rollingUpdate ? null : await AcquireOwnerMutationAsync(cancellationToken);
        cancellationToken = ownerMutation?.Token ?? cancellationToken;
        await _nameUniquenessDomainService.EnsureNameAvailableAsync(skill, cancellationToken);
        await using var preparedDirectory = await PrepareArchiveDirectoryAsync(skill.Name, skill.Description, archive);

        var originalContentPath = skill.ContentPath;
        var originalExtra = skill.Extra;
        var contentPath = GetUploadContentPath(skill);
        var targetDirectory = GetSkillAbsolutePath(contentPath);
        Guid? revision = _rollingUpdate ? null : Guid.CreateVersion7();
        var backupPath = await PublishUploadDirectoryAsync(
            skill.Name,
            preparedDirectory.DirectoryPath,
            targetDirectory,
            revision,
            cancellationToken
        );
        skill.ContentPath = contentPath;
        new SkillBehavior(skill).DefineLocalContent(sourceDigest, revision);

        try
        {
            await _dbContext.Skills.AddAsync(skill, cancellationToken);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            _dbContext.Skills.Entry(skill).State = EntityState.Detached;
            var persistedContent = await GetPersistedSkillContentAsync(skill.Id, exception);
            if (persistedContent?.ContentPath == contentPath && persistedContent.ContentRevision == revision)
            {
                SkillDirectoryReplacement.Complete(backupPath);
                throw;
            }

            skill.ContentPath = originalContentPath;
            skill.Extra = originalExtra;
            UndoDirectoryPublication(targetDirectory, backupPath);
            throw;
        }

        SkillDirectoryReplacement.Complete(backupPath);
        _logger.LogInformation("Created local skill {SkillName} at {SkillPath}", skill.Name, targetDirectory);
        return await GetAsync(skill.Id) ?? new SkillDetails(skill, [], false);
    }

    private async Task<SkillDetails> CreateRemoteAsync(Skill skill, CancellationToken cancellationToken)
    {
        var remoteUrl = _remoteSkillClient.NormalizeUrl(skill.RemoteUrl);
        var definition = await _remoteSkillClient.FetchAsync(remoteUrl, cancellationToken);
        new SkillBehavior(skill).DefineRemote(remoteUrl, definition.Name, definition.Description);
        await _nameUniquenessDomainService.EnsureNameAvailableAsync(skill, cancellationToken);

        await _dbContext.Skills.AddAsync(skill, cancellationToken);
        await _dbContext.RemoteSkillCaches.AddAsync(CreateCache(skill, definition), cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Created remote skill {SkillName} from {RemoteLocation}",
            skill.Name,
            GetRemoteLocationForLogging(remoteUrl)
        );
        return await GetAsync(skill.Id) ?? new SkillDetails(skill, [], false);
    }

    private async Task<SkillDetails?> UpdateLocalAsync(
        Skill existing,
        string name,
        string description,
        IFormFile? archive,
        string? sourceDigest,
        CancellationToken cancellationToken
    )
    {
        var original = new LocalSkillState(existing.Name, existing.Description, existing.ContentPath, existing.Extra);
        var originalPath = GetSkillAbsolutePath(existing);
        var originalIsDirect = IsDirectContentDirectory(originalPath);
        await using var ownerMutation =
            !_rollingUpdate || originalIsDirect ? await AcquireOwnerMutationAsync(cancellationToken) : null;
        cancellationToken = ownerMutation?.Token ?? cancellationToken;
        if (originalIsDirect)
        {
            SkillDirectoryReplacement.Recover(originalPath, existing.Extra.ContentRevision);
        }

        string? backupPath = null;

        try
        {
            var behavior = new SkillBehavior(existing);
            behavior.DefineLocal(name, description);
            await _nameUniquenessDomainService.EnsureNameAvailableAsync(existing, cancellationToken);

            if (archive != null)
            {
                await using var prepared = await PrepareArchiveDirectoryAsync(
                    existing.Name,
                    existing.Description,
                    archive
                );
                var contentPath = GetUploadContentPath(existing);
                var directory = GetSkillAbsolutePath(contentPath);
                Guid? revision = _rollingUpdate ? null : Guid.CreateVersion7();
                backupPath = await PublishUploadDirectoryAsync(
                    existing.Name,
                    prepared.DirectoryPath,
                    directory,
                    revision,
                    cancellationToken,
                    replacesCurrent: string.Equals(originalPath, directory, StringComparison.Ordinal)
                );

                existing.ContentPath = contentPath;
                behavior.DefineLocalContent(sourceDigest, revision);
            }

            _dbContext.Skills.Entry(existing).Property(skill => skill.Name).IsModified = true;
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            await RestoreLocalSkillAsync(existing, original, originalPath, backupPath, exception);
            throw;
        }

        SkillDirectoryReplacement.Complete(backupPath);
        await RetireDirectoryIfDifferentAsync(originalPath, GetSkillAbsolutePath(existing)).ConfigureAwait(false);

        _logger.LogInformation("Updated skill {SkillId} ({SkillName})", existing.Id, existing.Name);
        return await GetAsync(existing.Id);
    }

    private async Task RestoreLocalSkillAsync(
        Skill skill,
        LocalSkillState original,
        string originalPath,
        string? backupPath,
        Exception saveException
    )
    {
        var published =
            skill.ContentPath != original.ContentPath || skill.Extra.ContentRevision != original.Extra.ContentRevision;
        _dbContext.Skills.Entry(skill).State = EntityState.Detached;
        if (published)
        {
            var persistedContent = await GetPersistedSkillContentAsync(skill.Id, saveException);
            if (
                persistedContent?.ContentPath == skill.ContentPath
                && persistedContent.ContentRevision == skill.Extra.ContentRevision
            )
            {
                SkillDirectoryReplacement.Complete(backupPath);
                await RetireDirectoryIfDifferentAsync(originalPath, GetSkillAbsolutePath(skill)).ConfigureAwait(false);
                return;
            }

            UndoDirectoryPublication(GetSkillAbsolutePath(skill), backupPath);
        }

        skill.Name = original.Name;
        skill.Description = original.Description;
        skill.ContentPath = original.ContentPath;
        skill.Extra = original.Extra;
    }

    private sealed record LocalSkillState(string Name, string Description, string ContentPath, SkillExtra Extra);

    private sealed record LocalContentPointer(string ContentPath, SkillExtra Extra)
    {
        public Guid? ContentRevision => Extra.ContentRevision;
    }

    private async Task<SkillDetails?> UpdateRemoteAsync(
        Skill existing,
        string? remoteUrl,
        CancellationToken cancellationToken
    )
    {
        await using var lease = await _remoteSkillRefreshLock.AcquireAsync(existing.Id, cancellationToken);
        var normalizedUrl = _remoteSkillClient.NormalizeUrl(
            string.IsNullOrWhiteSpace(remoteUrl) ? existing.RemoteUrl : remoteUrl
        );
        var definition = await _remoteSkillClient.FetchAsync(normalizedUrl, cancellationToken);
        new SkillBehavior(existing).DefineRemote(normalizedUrl, definition.Name, definition.Description);
        await _nameUniquenessDomainService.EnsureNameAvailableAsync(existing, cancellationToken);

        var cache = await _dbContext.RemoteSkillCaches.SingleOrDefaultAsync(
            item => item.SkillId == existing.Id,
            cancellationToken
        );
        if (cache == null)
        {
            await _dbContext.RemoteSkillCaches.AddAsync(CreateCache(existing, definition), cancellationToken);
        }
        else
        {
            ApplyCache(cache, existing, definition);
        }

        _dbContext.Skills.Entry(existing).Property(skill => skill.Name).IsModified = true;
        await _dbContext.SaveChangesAsync(cancellationToken);
        _logger.LogInformation(
            "Updated remote skill {SkillId} ({SkillName}) from {RemoteLocation}",
            existing.Id,
            existing.Name,
            GetRemoteLocationForLogging(normalizedUrl)
        );
        return await GetAsync(existing.Id);
    }

    private static string GetRemoteLocationForLogging(string remoteUrl)
    {
        var uriBuilder = new UriBuilder(remoteUrl)
        {
            UserName = string.Empty,
            Password = string.Empty,
            Query = string.Empty,
            Fragment = string.Empty,
        };
        return uriBuilder.Uri.AbsoluteUri;
    }

    private async Task<bool> DeleteCoreAsync(Skill existing, CancellationToken cancellationToken)
    {
        var cache = await _dbContext.RemoteSkillCaches.SingleOrDefaultAsync(
            item => item.SkillId == existing.Id,
            cancellationToken
        );
        if (cache != null)
        {
            _dbContext.RemoteSkillCaches.Remove(cache);
        }

        await _agentReferences.RemoveSkillBindingsAsync(existing.Id, cancellationToken).ConfigureAwait(false);

        _dbContext.Skills.Remove(existing);
        await _dbContext.SaveChangesAsync(cancellationToken);

        if (existing.Kind == SkillKind.Local)
        {
            await _contentRetirement
                .RetireAsync(GetSkillContentLocation(existing), CancellationToken.None)
                .ConfigureAwait(false);
        }

        _logger.LogInformation("Deleted skill {SkillId} ({SkillName})", existing.Id, existing.Name);
        return true;
    }

    private async Task<IReadOnlyList<SkillDetails>> AttachAgentIdsAsync(IReadOnlyList<Skill> skills)
    {
        if (skills.Count == 0)
        {
            return [];
        }

        var skillIds = skills.Select(x => x.Id).ToHashSet();
        var map = await _agentReferences.GetAgentIdsBySkillIdsAsync(skillIds).ConfigureAwait(false);

        return skills
            .Select(skill => new SkillDetails(skill, map.GetValueOrDefault(skill.Id, []), IsBuiltIn(skill)))
            .ToList();
    }

    private bool IsBuiltIn(Skill skill) => new SkillBehavior(skill).IsBuiltIn(_builtInSkillIds.Contains(skill.Id));

    private string ResolveOwnerUserId() => _currentUser.RequiredUserId;

    private RemoteSkillCache CreateCache(Skill skill, RemoteSkillDefinition definition)
    {
        var cache = new RemoteSkillCache { SkillId = skill.Id };
        ApplyCache(cache, skill, definition);
        return cache;
    }

    private void ApplyCache(RemoteSkillCache cache, Skill skill, RemoteSkillDefinition definition)
    {
        cache.SourceUrl = skill.RemoteUrl!;
        cache.ContentJson = RemoteSkillDefinitionSerializer.Serialize(definition);
        cache.FetchedAt = _timeProvider.GetUtcNow();
    }

    private async Task<IReadOnlyList<Guid>> GetSkillAgentIdsAsync(Guid skillId)
    {
        var map = await _agentReferences.GetAgentIdsBySkillIdsAsync([skillId]).ConfigureAwait(false);
        return map.GetValueOrDefault(skillId, []);
    }

    private async Task<PreparedSkillDirectory> PrepareArchiveDirectoryAsync(
        string skillName,
        string description,
        IFormFile archive
    )
    {
        if (archive.Length == 0)
        {
            throw new AgwException(ErrorCodes.SkillArchiveCannotBeEmpty);
        }

        if (!string.Equals(Path.GetExtension(archive.FileName), ".zip", StringComparison.OrdinalIgnoreCase))
        {
            throw new AgwException(ErrorCodes.SkillArchiveMustBeZip);
        }

        var tempPath = _dataPaths.TempDirectory;
        if (!Directory.Exists(tempPath))
        {
            Directory.CreateDirectory(tempPath);
        }

        var extractionRoot = Path.Combine(tempPath, $"agw-skill-extract-{Guid.CreateVersion7():N}");
        var preparedRoot = Path.Combine(tempPath, $"{PreparedSkillDirectoryPrefix}{Guid.CreateVersion7():N}");
        Directory.CreateDirectory(extractionRoot);
        Directory.CreateDirectory(preparedRoot);

        try
        {
            await using (var archiveStream = archive.OpenReadStream())
            using (var zipArchive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: false))
            {
                ExtractArchive(zipArchive, extractionRoot);
            }

            var extractedSkillRoot = FindSkillRoot(extractionRoot);
            if (extractedSkillRoot == null)
            {
                throw new AgwException(
                    ErrorCodes.SkillArchiveMissingSkillMarkdown,
                    "The uploaded archive must contain a skill directory with a SKILL.md file."
                );
            }

            var targetDirectory = Path.Combine(preparedRoot, skillName);
            CopyDirectory(extractedSkillRoot, targetDirectory);
            RewriteSkillMarkdown(Path.Combine(targetDirectory, "SKILL.md"), skillName, description);

            return new PreparedSkillDirectory(preparedRoot, targetDirectory);
        }
        catch
        {
            SafeDeleteDirectory(extractionRoot);
            SafeDeleteDirectory(preparedRoot);
            throw;
        }
        finally
        {
            SafeDeleteDirectory(extractionRoot);
        }
    }

    private static void ExtractArchive(ZipArchive archive, string destinationRoot)
    {
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.FullName))
            {
                continue;
            }

            var normalizedPath = entry.FullName.Replace('\\', '/');
            if (normalizedPath.StartsWith("__MACOSX/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var destinationPath = Path.GetFullPath(Path.Combine(destinationRoot, normalizedPath));
            if (!destinationPath.StartsWith(Path.GetFullPath(destinationRoot), StringComparison.Ordinal))
            {
                throw new AgwException(ErrorCodes.SkillArchiveContainsInvalidPaths);
            }

            if (normalizedPath.EndsWith('/'))
            {
                Directory.CreateDirectory(destinationPath);
                continue;
            }

            var directory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using var entryStream = entry.Open();
            using var fileStream = File.Create(destinationPath);
            entryStream.CopyTo(fileStream);
        }
    }

    private static string? FindSkillRoot(string extractionRoot)
    {
        if (File.Exists(Path.Combine(extractionRoot, "SKILL.md")))
        {
            return extractionRoot;
        }

        var directories = Directory
            .EnumerateDirectories(extractionRoot, "*", SearchOption.TopDirectoryOnly)
            .Where(path => !string.Equals(Path.GetFileName(path), "__MACOSX", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (directories.Count == 1 && File.Exists(Path.Combine(directories[0], "SKILL.md")))
        {
            return directories[0];
        }

        return Directory
            .EnumerateFiles(extractionRoot, "SKILL.md", SearchOption.AllDirectories)
            .Select(Path.GetDirectoryName)
            .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
    }

    private static void RewriteSkillMarkdown(string filePath, string skillName, string description)
    {
        var content = File.ReadAllText(filePath);
        var normalized = content.Replace("\r\n", "\n");
        if (!normalized.StartsWith("---\n", StringComparison.Ordinal))
        {
            throw new AgwException(ErrorCodes.SkillMarkdownMissingFrontmatter);
        }

        var secondDelimiterIndex = normalized.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        if (secondDelimiterIndex < 0)
        {
            throw new AgwException(ErrorCodes.SkillMarkdownIncompleteFrontmatter);
        }

        var frontmatter = normalized[4..secondDelimiterIndex];
        var body = normalized[(secondDelimiterIndex + 5)..];
        var lines = frontmatter.Split('\n').ToList();
        var updatedLines = new List<string>();
        var replacedName = false;
        var hasDescription = false;

        foreach (var line in lines)
        {
            var nameSeparatorIndex = line.IndexOf(':');
            if (
                nameSeparatorIndex >= 0
                && string.Equals(line[..nameSeparatorIndex].TrimEnd(), "name", StringComparison.OrdinalIgnoreCase)
            )
            {
                updatedLines.Add($"name: {skillName}");
                replacedName = true;
                continue;
            }

            if (line.StartsWith("description:", StringComparison.OrdinalIgnoreCase))
            {
                updatedLines.Add(line);
                hasDescription = true;
                continue;
            }

            updatedLines.Add(line);
        }

        if (!replacedName)
        {
            updatedLines.Insert(0, $"name: {skillName}");
        }

        if (!hasDescription)
        {
            var escapedDescription = EscapeYamlDoubleQuotedString(description.Trim());
            updatedLines.Insert(replacedName ? 1 : 0, $"description: \"{escapedDescription}\"");
        }

        var rebuilt = new StringBuilder();
        rebuilt.AppendLine("---");
        foreach (var line in updatedLines)
        {
            rebuilt.AppendLine(line);
        }

        rebuilt.AppendLine("---");
        rebuilt.Append(body);
        File.WriteAllText(filePath, rebuilt.ToString());
    }

    private static string EscapeYamlDoubleQuotedString(string value)
    {
        return value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
    }

    private string GetSkillAbsolutePath(Skill skill) =>
        GetSkillAbsolutePath(
            string.IsNullOrWhiteSpace(skill.ContentPath)
                ? Path.Combine(Path.GetRelativePath(_dataPaths.Root, _dataPaths.SkillsDirectory), skill.Name)
                : skill.ContentPath
        );

    private string GetSkillAbsolutePath(string contentPath)
    {
        var relativePath = contentPath
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);
        var rootPath = Path.GetFullPath(_dataPaths.Root);
        var absolutePath = Path.GetFullPath(Path.Combine(rootPath, relativePath));
        var relativeToRoot = Path.GetRelativePath(rootPath, absolutePath);
        if (
            Path.IsPathRooted(relativeToRoot)
            || string.Equals(relativeToRoot, "..", StringComparison.Ordinal)
            || relativeToRoot.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        )
        {
            throw new AgwException(ErrorCodes.InvalidSkillDirectoryPath);
        }

        return absolutePath;
    }

    private async Task<LocalContentPointer?> GetPersistedSkillContentAsync(Guid skillId, Exception saveException)
    {
        try
        {
            return await _dbContext
                .Skills.AsNoTracking()
                .Where(skill => skill.Id == skillId)
                .Select(skill => new LocalContentPointer(skill.ContentPath, skill.Extra))
                .SingleOrDefaultAsync(CancellationToken.None);
        }
        catch (Exception confirmationException)
        {
            _logger.LogError(
                confirmationException,
                "Could not verify the committed content for skill {SkillId}",
                skillId
            );
            ExceptionDispatchInfo.Throw(saveException);
            throw;
        }
    }

    private string GetUploadContentPath(Skill skill) =>
        _rollingUpdate
            ? GetRollingContentPath(skill)
            : GetContentPath(SkillContentPaths.GetOwnerDirectoryName(ResolveOwnerUserId()), skill.Name);

    private string GetRollingContentPath(Skill skill) =>
        GetContentPath($"{skill.Name}-{Guid.CreateVersion7():N}", skill.Name);

    private string GetContentPath(string parentDirectory, string skillName)
    {
        var skillsDirectoryPath = Path.GetRelativePath(_dataPaths.Root, _dataPaths.SkillsDirectory)
            .Replace(Path.DirectorySeparatorChar, '/');
        return $"{skillsDirectoryPath}/{parentDirectory}/{skillName}";
    }

    private async Task<ApplicationLockScope> AcquireOwnerMutationAsync(CancellationToken cancellationToken) =>
        new(
            await _applicationLock.AcquireAsync(
                $"skill-content-owner:{SkillContentPaths.GetOwnerDirectoryName(ResolveOwnerUserId())}",
                cancellationToken
            ),
            cancellationToken
        );

    private bool IsDirectContentDirectory(string directory)
    {
        var parent = Directory.GetParent(directory);
        return parent?.Parent?.FullName == Path.GetFullPath(_dataPaths.SkillsDirectory)
            && SkillContentPaths.IsOwnerDirectoryName(parent.Name);
    }

    private async Task RetireDirectoryIfDifferentAsync(string sourceDirectory, string targetDirectory)
    {
        if (
            !string.Equals(
                Path.GetFullPath(sourceDirectory),
                Path.GetFullPath(targetDirectory),
                StringComparison.Ordinal
            )
        )
        {
            await _contentRetirement
                .RetireAsync(GetSkillContentLocation(sourceDirectory), CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    private SkillContentLocation GetSkillContentLocation(Skill skill) =>
        GetSkillContentLocation(GetSkillAbsolutePath(skill));

    private SkillContentLocation GetSkillContentLocation(string directory)
    {
        var absoluteDirectory = Path.GetFullPath(directory);
        var contentPath = Path.GetRelativePath(_dataPaths.Root, absoluteDirectory)
            .Replace(Path.DirectorySeparatorChar, '/');
        return new SkillContentLocation(contentPath, absoluteDirectory);
    }

    private void DeleteSkillContentDirectory(string directoryPath)
    {
        SafeDeleteDirectory(directoryPath);
        var parent = Directory.GetParent(directoryPath);
        var skillsRoot = Path.GetFullPath(_dataPaths.SkillsDirectory);
        if (
            parent?.Parent?.FullName == skillsRoot
            && !SkillContentPaths.IsOwnerDirectoryName(parent.Name)
            && parent.Exists
            && !parent.EnumerateFileSystemInfos().Any()
        )
        {
            parent.Delete();
        }
    }

    private void UndoDirectoryPublication(string directory, string? backupPath)
    {
        if (backupPath == null)
        {
            DeleteSkillContentDirectory(directory);
        }
        else
        {
            SkillDirectoryReplacement.RollBack(directory, backupPath);
        }
    }

    private async Task<string?> PublishUploadDirectoryAsync(
        string skillName,
        string sourceDirectory,
        string targetDirectory,
        Guid? revision,
        CancellationToken cancellationToken,
        bool replacesCurrent = false
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (revision == null)
        {
            PublishDirectory(sourceDirectory, targetDirectory);
            return null;
        }

        if (!replacesCurrent)
        {
            var location = GetSkillContentLocation(targetDirectory);
            var markerPath = targetDirectory + SkillContentPaths.RetiredMarkerSuffix;
            var isReferenced = await _dbContext.Skills.AnyAsync(
                skill => skill.ContentPath == location.ContentPath,
                cancellationToken
            );
            if (!isReferenced && !File.Exists(markerPath))
            {
                SkillDirectoryReplacement.Recover(targetDirectory, committedRevision: null);
                // 回收记录已移除但尚未退役的目录，保留仍有租约的文件。
                // Retire unreferenced content while preserving files that are still leased.
                await _contentRetirement.RetireAsync(location, cancellationToken);
            }

            if (isReferenced || File.Exists(markerPath) || Path.Exists(targetDirectory))
            {
                throw new AgwException(
                    ErrorCodes.SkillAlreadyExists,
                    $"The directory for skill '{skillName}' is still in use."
                );
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var backupPath = SkillDirectoryReplacement.Backup(targetDirectory, revision.Value);
        try
        {
            PublishDirectory(sourceDirectory, targetDirectory);
        }
        catch
        {
            SkillDirectoryReplacement.RollBack(targetDirectory, backupPath);
            throw;
        }

        return backupPath;
    }

    private static void PublishDirectory(string sourceDirectory, string targetDirectory)
    {
        var parentDirectory = Path.GetDirectoryName(targetDirectory);
        if (string.IsNullOrWhiteSpace(parentDirectory))
        {
            throw new AgwException(ErrorCodes.InvalidSkillDirectoryPath);
        }

        Directory.CreateDirectory(parentDirectory);
        Directory.Move(sourceDirectory, targetDirectory);
    }

    private static void CopyDirectory(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);

        foreach (var directory in Directory.GetDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, directory);
            Directory.CreateDirectory(Path.Combine(destinationDirectory, relativePath));
        }

        foreach (var file in Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, file);
            var destinationPath = Path.Combine(destinationDirectory, relativePath);
            var destinationFolder = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(destinationFolder))
            {
                Directory.CreateDirectory(destinationFolder);
            }

            File.Copy(file, destinationPath, overwrite: true);
        }
    }

    private static void SafeDeleteDirectory(string? directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
        {
            return;
        }

        Directory.Delete(directoryPath, recursive: true);
    }

    private sealed class PreparedSkillDirectory : IAsyncDisposable
    {
        private readonly string _rootDirectory;

        public PreparedSkillDirectory(string rootDirectory, string directoryPath)
        {
            _rootDirectory = rootDirectory;
            DirectoryPath = directoryPath;
        }

        public string DirectoryPath { get; private set; }

        public ValueTask DisposeAsync()
        {
            SafeDeleteDirectory(_rootDirectory);
            return ValueTask.CompletedTask;
        }
    }
}
