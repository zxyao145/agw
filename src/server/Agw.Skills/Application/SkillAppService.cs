using System.IO.Compression;
using System.Text;
using Agw.Agents.Contracts.Catalog;
using Agw.Shared.Contracts;
using Agw.Shared.Contracts.Pagination;
using Agw.Shared.Data.Entities.Skills;
using Agw.Shared.Data.Pagination;
using Agw.Shared.Exceptions;
using Agw.Shared.Runtime;
using Agw.Skills.Application.Persistence;
using Agw.Skills.Application.Remote;
using Agw.Skills.Contracts.Registration;
using Agw.Skills.Contracts.Remote;
using Agw.Skills.Domain.Rules;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Agw.Skills.Application;

public sealed record SkillDetails(Skill Skill, IReadOnlyList<Guid> AgentIds, bool IsBuiltIn);

public class SkillAppService
{
    private readonly ISkillsDbContext _dbContext;
    private readonly IAgentReferenceFacade _agentReferences;
    private readonly AgwDataPaths _dataPaths;
    private readonly ILogger<SkillAppService> _logger;
    private readonly IReadOnlySet<Guid> _builtInSkillIds;
    private readonly IRemoteSkillClient _remoteSkillClient;
    private readonly IRemoteSkillRefreshLock _remoteSkillRefreshLock;
    private readonly TimeProvider _timeProvider;
    private readonly ICurrentUser _currentUser;

    public SkillAppService(
        ISkillsDbContext dbContext,
        IAgentReferenceFacade agentReferences,
        AgwDataPaths dataPaths,
        ILogger<SkillAppService> logger,
        IRemoteSkillClient remoteSkillClient,
        IRemoteSkillRefreshLock remoteSkillRefreshLock,
        TimeProvider timeProvider,
        ICurrentUser currentUser,
        IEnumerable<IAgentSkillRegistration>? skillRegistrations = null
    )
    {
        _dbContext = dbContext;
        _agentReferences = agentReferences;
        _dataPaths = dataPaths;
        _logger = logger;
        _remoteSkillClient = remoteSkillClient;
        _remoteSkillRefreshLock = remoteSkillRefreshLock;
        _timeProvider = timeProvider;
        _currentUser = currentUser;
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

    public async Task<SkillDetails> CreateAsync(
        Skill skill,
        IFormFile? archive,
        string user,
        string? remoteUrl = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(skill);

        skill.RemoteUrl = remoteUrl;
        return skill.Kind switch
        {
            SkillKind.Local => await CreateLocalAsync(skill, archive, cancellationToken),
            SkillKind.Remote => await CreateRemoteAsync(skill, archive, cancellationToken),
            _ => throw new AgwException(ErrorCodes.SkillKindInvalid),
        };
    }

    public async Task<SkillDetails?> UpdateAsync(
        Guid id,
        string name,
        string description,
        IFormFile? archive,
        string user,
        string? remoteUrl = null,
        CancellationToken cancellationToken = default
    )
    {
        var ownerUserId = ResolveOwnerUserId();
        var existing = await _dbContext.Skills.SingleOrDefaultAsync(skill =>
            skill.Id == id && (skill.Kind == SkillKind.BuiltIn || skill.CreateBy == ownerUserId)
        );
        if (existing == null)
        {
            return null;
        }

        EnsureMutable(existing);
        return existing.Kind switch
        {
            SkillKind.Local => await UpdateLocalAsync(
                existing,
                name,
                description,
                archive,
                remoteUrl,
                cancellationToken
            ),
            SkillKind.Remote => await UpdateRemoteAsync(existing, archive, remoteUrl, cancellationToken),
            _ => throw new AgwException(ErrorCodes.SkillKindInvalid),
        };
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var ownerUserId = ResolveOwnerUserId();
        var existing = await _dbContext
            .Skills.AsNoTracking()
            .FirstOrDefaultAsync(skill =>
                skill.Id == id && (skill.Kind == SkillKind.BuiltIn || skill.CreateBy == ownerUserId)
            );
        if (existing == null)
        {
            return false;
        }

        EnsureMutable(existing);
        if (existing.Kind == SkillKind.Remote)
        {
            await using var lease = await _remoteSkillRefreshLock.AcquireAsync(existing.Id, cancellationToken);
            return await DeleteCoreAsync(existing, cancellationToken);
        }

        return await DeleteCoreAsync(existing, cancellationToken);
    }

    private async Task<SkillDetails> CreateLocalAsync(
        Skill skill,
        IFormFile? archive,
        CancellationToken cancellationToken
    )
    {
        if (archive == null)
        {
            throw new AgwException(ErrorCodes.SkillArchiveCannotBeEmpty);
        }

        if (!string.IsNullOrWhiteSpace(skill.RemoteUrl))
        {
            throw new AgwException(ErrorCodes.SkillKindInvalid, "Local skills cannot define a remote URL.");
        }

        await EnsureNameAvailableAsync(skill.Name, null, cancellationToken);
        skill.RemoteUrl = null;
        SkillRules.Validate(skill.Name, skill.Description);
        skill.Id = skill.Id == Guid.Empty ? Guid.CreateVersion7() : skill.Id;
        skill.ContentPath = SkillRules.GetContentPath(skill.Kind, skill.Id);
        await using var preparedDirectory = await PrepareArchiveDirectoryAsync(skill.Name, skill.Description, archive);

        await _dbContext.Skills.AddAsync(skill, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);

        var targetDirectory = GetSkillAbsolutePath(skill);
        ReplaceDirectory(targetDirectory, preparedDirectory.DirectoryPath);

        _logger.LogInformation("Created local skill {SkillName} at {SkillPath}", skill.Name, targetDirectory);
        return await GetAsync(skill.Id) ?? new SkillDetails(skill, [], false);
    }

    private async Task<SkillDetails> CreateRemoteAsync(
        Skill skill,
        IFormFile? archive,
        CancellationToken cancellationToken
    )
    {
        if (archive != null)
        {
            throw new AgwException(ErrorCodes.RemoteSkillArchiveNotAllowed);
        }

        var remoteUrl = _remoteSkillClient.NormalizeUrl(skill.RemoteUrl);
        var definition = await _remoteSkillClient.FetchAsync(remoteUrl, cancellationToken);
        await EnsureNameAvailableAsync(definition.Name, null, cancellationToken);

        skill.Name = definition.Name;
        skill.Description = definition.Description;
        skill.RemoteUrl = remoteUrl;
        SkillRules.Validate(skill.Name, skill.Description);
        skill.Id = skill.Id == Guid.Empty ? Guid.CreateVersion7() : skill.Id;
        skill.ContentPath = SkillRules.GetContentPath(skill.Kind, skill.Id);
        await _dbContext.Skills.AddAsync(skill, cancellationToken);
        await _dbContext.RemoteSkillCaches.AddAsync(CreateCache(skill, definition), cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Created remote skill {SkillName} from {RemoteLocation}",
            skill.Name,
            GetRemoteLocationForLogging(skill.RemoteUrl)
        );
        return await GetAsync(skill.Id) ?? new SkillDetails(skill, [], false);
    }

    private async Task<SkillDetails?> UpdateLocalAsync(
        Skill existing,
        string name,
        string description,
        IFormFile? archive,
        string? remoteUrl,
        CancellationToken cancellationToken
    )
    {
        if (!string.IsNullOrWhiteSpace(remoteUrl))
        {
            throw new AgwException(ErrorCodes.SkillKindInvalid, "Local skills cannot define a remote URL.");
        }

        var normalizedName = name.Trim();
        if (!string.Equals(existing.Name, normalizedName, StringComparison.Ordinal) && archive == null)
        {
            throw new AgwException(
                ErrorCodes.SkillNameUpdateRequiresArchive,
                "Updating the skill name requires uploading a new archive so SKILL.md can stay consistent."
            );
        }

        await EnsureNameAvailableAsync(normalizedName, existing.Id, cancellationToken);

        var originalPath = GetSkillAbsolutePath(existing);
        SkillRules.Validate(normalizedName, description);
        existing.Name = normalizedName;
        existing.Description = description.Trim();
        existing.ContentPath = SkillRules.GetContentPath(existing.Kind, existing.Id);
        var targetPath = GetSkillAbsolutePath(existing);

        PreparedSkillDirectory? preparedDirectory = null;
        if (archive != null)
        {
            preparedDirectory = await PrepareArchiveDirectoryAsync(existing.Name, existing.Description, archive);
        }

        _dbContext.Skills.Entry(existing).Property(skill => skill.Name).IsModified = true;
        await _dbContext.SaveChangesAsync(cancellationToken);

        if (preparedDirectory != null)
        {
            await using (preparedDirectory)
            {
                ReplaceDirectory(targetPath, preparedDirectory.DirectoryPath);
            }
            DeleteDirectoryIfDifferent(originalPath, targetPath);
        }
        else if (!string.Equals(originalPath, targetPath, StringComparison.Ordinal))
        {
            if (Directory.Exists(originalPath))
            {
                ReplaceDirectory(targetPath, originalPath, moveSourceDirectory: true);
            }
        }

        _logger.LogInformation("Updated skill {SkillId} ({SkillName})", existing.Id, existing.Name);
        return await GetAsync(existing.Id);
    }

    private async Task<SkillDetails?> UpdateRemoteAsync(
        Skill existing,
        IFormFile? archive,
        string? remoteUrl,
        CancellationToken cancellationToken
    )
    {
        if (archive != null)
        {
            throw new AgwException(ErrorCodes.RemoteSkillArchiveNotAllowed);
        }

        await using var lease = await _remoteSkillRefreshLock.AcquireAsync(existing.Id, cancellationToken);
        var normalizedUrl = _remoteSkillClient.NormalizeUrl(
            string.IsNullOrWhiteSpace(remoteUrl) ? existing.RemoteUrl : remoteUrl
        );
        var definition = await _remoteSkillClient.FetchAsync(normalizedUrl, cancellationToken);
        await EnsureNameAvailableAsync(definition.Name, existing.Id, cancellationToken);

        existing.RemoteUrl = normalizedUrl;
        SkillRules.Validate(definition.Name, definition.Description);
        existing.Name = definition.Name.Trim();
        existing.Description = definition.Description.Trim();
        existing.ContentPath = SkillRules.GetContentPath(existing.Kind, existing.Id);
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
            GetRemoteLocationForLogging(existing.RemoteUrl)
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
            var targetDirectory = GetSkillAbsolutePath(existing);
            if (Directory.Exists(targetDirectory))
            {
                Directory.Delete(targetDirectory, recursive: true);
            }
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

    private void EnsureMutable(Skill skill)
    {
        if (IsBuiltIn(skill))
        {
            throw new AgwException(ErrorCodes.BuiltInSkillImmutable);
        }
    }

    private bool IsBuiltIn(Skill skill) => skill.Kind == SkillKind.BuiltIn || _builtInSkillIds.Contains(skill.Id);

    private string ResolveOwnerUserId() => _currentUser.RequiredUserId;

    private async Task EnsureNameAvailableAsync(string name, Guid? excludedSkillId, CancellationToken cancellationToken)
    {
        var ownerUserId = ResolveOwnerUserId();
        var existing = await _dbContext.Skills.SingleOrDefaultAsync(
            skill =>
                skill.Name == name
                && (skill.Kind == SkillKind.BuiltIn || skill.CreateBy == ownerUserId)
                && (!excludedSkillId.HasValue || skill.Id != excludedSkillId.Value),
            cancellationToken
        );
        if (existing != null)
        {
            throw new AgwException(ErrorCodes.SkillAlreadyExists, $"Skill '{name}' already exists.");
        }
    }

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
        var preparedRoot = Path.Combine(tempPath, $"agw-skill-prepared-{Guid.CreateVersion7():N}");
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
            if (line.StartsWith("name:", StringComparison.Ordinal))
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

    private string GetSkillAbsolutePath(Skill skill)
    {
        var relativePath = string.IsNullOrWhiteSpace(skill.ContentPath)
            ? Path.Combine("skills", skill.Name)
            : skill.ContentPath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
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

    private static void DeleteDirectoryIfDifferent(string sourceDirectory, string targetDirectory)
    {
        if (
            !string.Equals(
                Path.GetFullPath(sourceDirectory),
                Path.GetFullPath(targetDirectory),
                StringComparison.Ordinal
            ) && Directory.Exists(sourceDirectory)
        )
        {
            Directory.Delete(sourceDirectory, recursive: true);
        }
    }

    private static void ReplaceDirectory(
        string targetDirectory,
        string sourceDirectory,
        bool moveSourceDirectory = false
    )
    {
        var parentDirectory = Path.GetDirectoryName(targetDirectory);
        if (string.IsNullOrWhiteSpace(parentDirectory))
        {
            throw new AgwException(ErrorCodes.InvalidSkillDirectoryPath);
        }

        Directory.CreateDirectory(parentDirectory);
        if (Directory.Exists(targetDirectory))
        {
            Directory.Delete(targetDirectory, recursive: true);
        }

        if (moveSourceDirectory)
        {
            Directory.Move(sourceDirectory, targetDirectory);
            return;
        }

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
