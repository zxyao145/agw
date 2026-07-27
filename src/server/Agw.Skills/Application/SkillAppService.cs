using System.IO.Compression;
using System.Text;

using Agw.Agents.Execution.Agents.Skills;
using Agw.Domain.Services.Skills;
using Agw.Shared.Contracts.Pagination;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Skills;
using Agw.Shared.Data.Repositories;
using Agw.Shared.Exceptions;
using Agw.Shared.Pagination;
using Agw.Shared.Runtime;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Agw.Skills.Application;

public sealed record SkillDetails(
    Skill Skill,
    IReadOnlyList<Guid> AgentIds,
    bool IsBuiltIn);

public class SkillAppService
{
    private readonly IRepository<Skill> _skillRepository;
    private readonly IRepository<Agent> _agentRepository;
    private readonly IRepository<AgentSkillRelation> _agentSkillRelationRepository;
    private readonly IRepository<RemoteSkillCache> _remoteSkillCacheRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly SkillDomainService _skillDomainService;
    private readonly AgwDataPaths _dataPaths;
    private readonly ILogger<SkillAppService> _logger;
    private readonly IReadOnlySet<Guid> _builtInSkillIds;
    private readonly IRemoteSkillClient _remoteSkillClient;
    private readonly IRemoteSkillRefreshLock _remoteSkillRefreshLock;
    private readonly TimeProvider _timeProvider;

    public SkillAppService(
        IRepository<Skill> skillRepository,
        IRepository<Agent> agentRepository,
        IRepository<AgentSkillRelation> agentSkillRelationRepository,
        IRepository<RemoteSkillCache> remoteSkillCacheRepository,
        IUnitOfWork unitOfWork,
        SkillDomainService skillDomainService,
        AgwDataPaths dataPaths,
        ILogger<SkillAppService> logger,
        IRemoteSkillClient remoteSkillClient,
        IRemoteSkillRefreshLock remoteSkillRefreshLock,
        TimeProvider timeProvider,
        IEnumerable<IAgentSkillRegistration>? skillRegistrations = null)
    {
        _skillRepository = skillRepository;
        _agentRepository = agentRepository;
        _agentSkillRelationRepository = agentSkillRelationRepository;
        _remoteSkillCacheRepository = remoteSkillCacheRepository;
        _unitOfWork = unitOfWork;
        _skillDomainService = skillDomainService;
        _dataPaths = dataPaths;
        _logger = logger;
        _remoteSkillClient = remoteSkillClient;
        _remoteSkillRefreshLock = remoteSkillRefreshLock;
        _timeProvider = timeProvider;
        _builtInSkillIds = (skillRegistrations ?? [])
            .Select(registration => registration.Id)
            .ToHashSet();
    }

    public async Task<IReadOnlyList<SkillDetails>> ListAsync()
    {
        var skills = await _skillRepository.ListAsync();
        return await AttachAgentIdsAsync(skills);
    }

    public async Task<PagedResult<SkillDetails>> ListPageAsync(
        int pageIndex,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var page = await UpdatedTimePagination.ToPagedResultAsync(
            _skillRepository.Queryable,
            skill => skill.Id,
            pageIndex,
            pageSize,
            cancellationToken);
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
        var skill = await _skillRepository.GetByIdAsync(id);
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
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(skill);

        skill.RemoteUrl = remoteUrl;
        return skill.Kind switch
        {
            SkillKind.Local => await CreateLocalAsync(
                skill,
                archive,
                user,
                cancellationToken),
            SkillKind.Remote => await CreateRemoteAsync(
                skill,
                archive,
                user,
                cancellationToken),
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
        CancellationToken cancellationToken = default)
    {
        var existing = await _skillRepository.GetByIdAsync(id);
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
                user,
                cancellationToken),
            SkillKind.Remote => await UpdateRemoteAsync(
                existing,
                archive,
                remoteUrl,
                user,
                cancellationToken),
            _ => throw new AgwException(ErrorCodes.SkillKindInvalid),
        };
    }

    public async Task<bool> DeleteAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var existing = await _skillRepository.GetByIdAsync(id);
        if (existing == null)
        {
            return false;
        }

        EnsureMutable(existing);
        if (existing.Kind == SkillKind.Remote)
        {
            await using var lease = await _remoteSkillRefreshLock.AcquireAsync(
                existing.Id,
                cancellationToken);
            return await DeleteCoreAsync(existing, cancellationToken);
        }

        return await DeleteCoreAsync(existing, cancellationToken);
    }

    private async Task<SkillDetails> CreateLocalAsync(
        Skill skill,
        IFormFile? archive,
        string user,
        CancellationToken cancellationToken)
    {
        if (archive == null)
        {
            throw new AgwException(ErrorCodes.SkillArchiveCannotBeEmpty);
        }

        if (!string.IsNullOrWhiteSpace(skill.RemoteUrl))
        {
            throw new AgwException(
                ErrorCodes.SkillKindInvalid,
                "Local skills cannot define a remote URL.");
        }

        await EnsureNameAvailableAsync(skill.Name, null, cancellationToken);
        skill.RemoteUrl = null;
        _skillDomainService.PrepareForCreate(skill, user);
        await using var preparedDirectory = await PrepareArchiveDirectoryAsync(
            skill.Name,
            skill.Description,
            archive);

        await _skillRepository.AddAsync(skill);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        var targetDirectory = GetSkillAbsolutePath(skill.Name);
        ReplaceDirectory(targetDirectory, preparedDirectory.DirectoryPath);

        _logger.LogInformation(
            "Created local skill {SkillName} at {SkillPath}",
            skill.Name,
            targetDirectory);
        return await GetAsync(skill.Id) ?? new SkillDetails(skill, [], false);
    }

    private async Task<SkillDetails> CreateRemoteAsync(
        Skill skill,
        IFormFile? archive,
        string user,
        CancellationToken cancellationToken)
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
        _skillDomainService.PrepareForCreate(skill, user);
        await _skillRepository.AddAsync(skill);
        await _remoteSkillCacheRepository.AddAsync(CreateCache(skill, definition));
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Created remote skill {SkillName} from {RemoteLocation}",
            skill.Name,
            GetRemoteLocationForLogging(skill.RemoteUrl));
        return await GetAsync(skill.Id) ?? new SkillDetails(skill, [], false);
    }

    private async Task<SkillDetails?> UpdateLocalAsync(
        Skill existing,
        string name,
        string description,
        IFormFile? archive,
        string? remoteUrl,
        string user,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(remoteUrl))
        {
            throw new AgwException(
                ErrorCodes.SkillKindInvalid,
                "Local skills cannot define a remote URL.");
        }

        var normalizedName = name.Trim();
        if (!string.Equals(existing.Name, normalizedName, StringComparison.Ordinal) && archive == null)
        {
            throw new AgwException(ErrorCodes.SkillNameUpdateRequiresArchive, "Updating the skill name requires uploading a new archive so SKILL.md can stay consistent.");
        }

        await EnsureNameAvailableAsync(
            normalizedName,
            existing.Id,
            cancellationToken);

        var originalName = existing.Name;
        _skillDomainService.ApplyUpdate(existing, normalizedName, description, user);

        PreparedSkillDirectory? preparedDirectory = null;
        if (archive != null)
        {
            preparedDirectory = await PrepareArchiveDirectoryAsync(existing.Name, existing.Description, archive);
        }

        _skillRepository.Update(existing);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        if (preparedDirectory != null)
        {
            await using (preparedDirectory)
            {
                ReplaceDirectory(GetSkillAbsolutePath(existing.Name), preparedDirectory.DirectoryPath);
            }
        }
        else if (!string.Equals(originalName, existing.Name, StringComparison.Ordinal))
        {
            var sourceDirectory = GetSkillAbsolutePath(originalName);
            var targetDirectory = GetSkillAbsolutePath(existing.Name);
            if (Directory.Exists(sourceDirectory))
            {
                ReplaceDirectory(targetDirectory, sourceDirectory, moveSourceDirectory: true);
            }
        }

        _logger.LogInformation("Updated skill {SkillId} ({SkillName})", existing.Id, existing.Name);
        return await GetAsync(existing.Id);
    }

    private async Task<SkillDetails?> UpdateRemoteAsync(
        Skill existing,
        IFormFile? archive,
        string? remoteUrl,
        string user,
        CancellationToken cancellationToken)
    {
        if (archive != null)
        {
            throw new AgwException(ErrorCodes.RemoteSkillArchiveNotAllowed);
        }

        await using var lease = await _remoteSkillRefreshLock.AcquireAsync(
            existing.Id,
            cancellationToken);
        var normalizedUrl = _remoteSkillClient.NormalizeUrl(
            string.IsNullOrWhiteSpace(remoteUrl) ? existing.RemoteUrl : remoteUrl);
        var definition = await _remoteSkillClient.FetchAsync(
            normalizedUrl,
            cancellationToken);
        await EnsureNameAvailableAsync(
            definition.Name,
            existing.Id,
            cancellationToken);

        existing.RemoteUrl = normalizedUrl;
        _skillDomainService.ApplyUpdate(
            existing,
            definition.Name,
            definition.Description,
            user);
        _skillRepository.Update(existing);
        var cache = await _remoteSkillCacheRepository.GetByIdAsync(existing.Id);
        if (cache == null)
        {
            await _remoteSkillCacheRepository.AddAsync(
                CreateCache(existing, definition));
        }
        else
        {
            ApplyCache(cache, existing, definition);
            _remoteSkillCacheRepository.Update(cache);
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);
        _logger.LogInformation(
            "Updated remote skill {SkillId} ({SkillName}) from {RemoteLocation}",
            existing.Id,
            existing.Name,
            GetRemoteLocationForLogging(existing.RemoteUrl));
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

    private async Task<bool> DeleteCoreAsync(
        Skill existing,
        CancellationToken cancellationToken)
    {
        var cache = await _remoteSkillCacheRepository.GetByIdAsync(existing.Id);
        if (cache != null)
        {
            _remoteSkillCacheRepository.Remove(cache);
        }

        var relations = await _agentSkillRelationRepository.ListAsync(
            x => x.SkillId == existing.Id);
        foreach (var relation in relations)
        {
            _agentSkillRelationRepository.Remove(relation);
        }

        _skillRepository.Remove(existing);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        if (existing.Kind == SkillKind.Local)
        {
            var targetDirectory = GetSkillAbsolutePath(existing.Name);
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
        var relations = await _agentSkillRelationRepository.ListAsync(x => skillIds.Contains(x.SkillId));
        var map = relations
            .GroupBy(x => x.SkillId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<Guid>)g.Select(x => x.AgentId).ToList());

        return skills
            .Select(skill => new SkillDetails(
                skill,
                map.GetValueOrDefault(skill.Id, []),
                IsBuiltIn(skill)))
            .ToList();
    }

    private void EnsureMutable(Skill skill)
    {
        if (IsBuiltIn(skill))
        {
            throw new AgwException(ErrorCodes.BuiltInSkillImmutable);
        }
    }

    private bool IsBuiltIn(Skill skill) =>
        skill.Kind == SkillKind.BuiltIn || _builtInSkillIds.Contains(skill.Id);

    private async Task EnsureNameAvailableAsync(
        string name,
        Guid? excludedSkillId,
        CancellationToken cancellationToken)
    {
        var existing = await _skillRepository.SingleOrDefaultAsync(
            skill =>
                skill.Name == name &&
                (!excludedSkillId.HasValue || skill.Id != excludedSkillId.Value),
            cancellationToken);
        if (existing != null)
        {
            throw new AgwException(
                ErrorCodes.SkillAlreadyExists,
                $"Skill '{name}' already exists.");
        }
    }

    private RemoteSkillCache CreateCache(
        Skill skill,
        RemoteSkillDefinition definition)
    {
        var cache = new RemoteSkillCache { SkillId = skill.Id };
        ApplyCache(cache, skill, definition);
        return cache;
    }

    private void ApplyCache(
        RemoteSkillCache cache,
        Skill skill,
        RemoteSkillDefinition definition)
    {
        cache.SourceUrl = skill.RemoteUrl!;
        cache.ContentJson = RemoteSkillDefinitionSerializer.Serialize(definition);
        cache.FetchedAt = _timeProvider.GetUtcNow();
    }

    private async Task<IReadOnlyList<Guid>> GetSkillAgentIdsAsync(Guid skillId)
    {
        var relations = await _agentSkillRelationRepository.ListAsync(x => x.SkillId == skillId);
        return relations.Select(x => x.AgentId).ToList();
    }

    private async Task SyncAgentSkillRelationsAsync(Guid skillId, IEnumerable<Guid>? agentIds)
    {
        var existingLinks = await _agentSkillRelationRepository.ListAsync(x => x.SkillId == skillId);
        foreach (var link in existingLinks)
        {
            _agentSkillRelationRepository.Remove(link);
        }

        var requestedAgentIds = _skillDomainService.NormalizeAgentIds(agentIds);
        if (requestedAgentIds.Count == 0)
        {
            return;
        }

        var existingAgents = await _agentRepository.ListAsync(x => requestedAgentIds.Contains(x.Id));
        foreach (var agentId in existingAgents.Select(x => x.Id))
        {
            await _agentSkillRelationRepository.AddAsync(new AgentSkillRelation
            {
                AgentId = agentId,
                SkillId = skillId
            });
        }
    }

    private async Task<PreparedSkillDirectory> PrepareArchiveDirectoryAsync(string skillName, string description, IFormFile archive)
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
                throw new AgwException(ErrorCodes.SkillArchiveMissingSkillMarkdown, "The uploaded archive must contain a skill directory with a SKILL.md file.");
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
        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
    }

    private string GetSkillAbsolutePath(string skillName)
    {
        return Path.Combine(_dataPaths.SkillsDirectory, skillName);
    }

    private static void ReplaceDirectory(string targetDirectory, string sourceDirectory, bool moveSourceDirectory = false)
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
