using System.IO.Compression;
using System.Text;

using Agw.Domain.Services.Skills;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Skills;
using Agw.Shared.Data.Repositories;
using Agw.Shared.Exceptions;
using Agw.Shared.Runtime;

using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Agw.Skills.Application;

public sealed record SkillDetails(Skill Skill, IReadOnlyList<Guid> AgentIds);

public class SkillAppService
{
    private readonly IRepository<Skill> _skillRepository;
    private readonly IRepository<Agent> _agentRepository;
    private readonly IRepository<AgentSkillRelation> _agentSkillRelationRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly SkillDomainService _skillDomainService;
    private readonly AgwDataPaths _dataPaths;
    private readonly ILogger<SkillAppService> _logger;

    public SkillAppService(
        IRepository<Skill> skillRepository,
        IRepository<Agent> agentRepository,
        IRepository<AgentSkillRelation> agentSkillRelationRepository,
        IUnitOfWork unitOfWork,
        SkillDomainService skillDomainService,
        AgwDataPaths dataPaths,
        ILogger<SkillAppService> logger)
    {
        _skillRepository = skillRepository;
        _agentRepository = agentRepository;
        _agentSkillRelationRepository = agentSkillRelationRepository;
        _unitOfWork = unitOfWork;
        _skillDomainService = skillDomainService;
        _dataPaths = dataPaths;
        _logger = logger;
    }

    public async Task<IReadOnlyList<SkillDetails>> ListAsync()
    {
        var skills = await _skillRepository.ListAsync();
        return await AttachAgentIdsAsync(skills);
    }

    public async Task<SkillDetails?> GetAsync(Guid id)
    {
        var skill = await _skillRepository.GetByIdAsync(id);
        if (skill == null)
        {
            return null;
        }

        var agentIds = await GetSkillAgentIdsAsync(skill.Id);
        return new SkillDetails(skill, agentIds);
    }

    public async Task<SkillDetails> CreateAsync(Skill skill, IFormFile archive, string user)
    {
        ArgumentNullException.ThrowIfNull(skill);
        ArgumentNullException.ThrowIfNull(archive);

        if (await _skillRepository.Queryable.AnyAsync(x => x.Name == skill.Name))
        {
            throw new AgwException(ErrorCodes.SkillAlreadyExists, $"Skill '{skill.Name}' already exists.");
        }

        _skillDomainService.PrepareForCreate(skill, user);
        await using var preparedDirectory = await PrepareArchiveDirectoryAsync(skill.Name, skill.Description, archive);

        await _skillRepository.AddAsync(skill);
        await _unitOfWork.SaveChangesAsync();

        var targetDirectory = GetSkillAbsolutePath(skill.Name);
        ReplaceDirectory(targetDirectory, preparedDirectory.DirectoryPath);

        _logger.LogInformation("Created skill {SkillName} at {SkillPath}", skill.Name, targetDirectory);
        return await GetAsync(skill.Id) ?? new SkillDetails(skill, []);
    }

    public async Task<SkillDetails?> UpdateAsync(Guid id, string name, string description, IFormFile? archive, string user)
    {
        var existing = await _skillRepository.GetByIdAsync(id);
        if (existing == null)
        {
            return null;
        }

        var normalizedName = name.Trim();
        if (!string.Equals(existing.Name, normalizedName, StringComparison.Ordinal) && archive == null)
        {
            throw new AgwException(ErrorCodes.SkillNameUpdateRequiresArchive, "Updating the skill name requires uploading a new archive so SKILL.md can stay consistent.");
        }

        if (await _skillRepository.Queryable.AnyAsync(x => x.Id != id && x.Name == normalizedName))
        {
            throw new AgwException(ErrorCodes.SkillAlreadyExists, $"Skill '{normalizedName}' already exists.");
        }

        var originalName = existing.Name;
        _skillDomainService.ApplyUpdate(existing, normalizedName, description, user);

        PreparedSkillDirectory? preparedDirectory = null;
        if (archive != null)
        {
            preparedDirectory = await PrepareArchiveDirectoryAsync(existing.Name, existing.Description, archive);
        }

        _skillRepository.Update(existing);
        await _unitOfWork.SaveChangesAsync();

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

    public async Task<bool> DeleteAsync(Guid id)
    {
        var existing = await _skillRepository.GetByIdAsync(id);
        if (existing == null)
        {
            return false;
        }

        var relations = await _agentSkillRelationRepository.ListAsync(x => x.SkillId == id);
        foreach (var relation in relations)
        {
            _agentSkillRelationRepository.Remove(relation);
        }

        _skillRepository.Remove(existing);
        await _unitOfWork.SaveChangesAsync();

        var targetDirectory = GetSkillAbsolutePath(existing.Name);
        if (Directory.Exists(targetDirectory))
        {
            Directory.Delete(targetDirectory, recursive: true);
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
            .Select(skill => new SkillDetails(skill, map.GetValueOrDefault(skill.Id, [])))
            .ToList();
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

        var extractionRoot = Path.Combine(tempPath, $"agw-skill-extract-{Guid.NewGuid():N}");
        var preparedRoot = Path.Combine(tempPath, $"agw-skill-prepared-{Guid.NewGuid():N}");
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
