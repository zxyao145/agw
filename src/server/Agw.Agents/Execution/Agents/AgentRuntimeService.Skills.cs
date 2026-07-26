using Agw.Agents.Execution.Agents.Skills;
using Agw.Integrations.Application.Capabilities;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Data.Entities.Skills;

using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;

namespace Agw.Agents.Execution.Agents;

public partial class AgentRuntimeService
{
    private const string SkillsInstructionPrompt =
        """
        # Skills

        The following skills are available:

        {skills}

        Skill usage rules:

        - Use `load_skill` to load a skill's complete instructions.
        - Skill files are stored outside the project workspace.
        - Never use bash, glob, ls, or project file tools to locate skill files.
        - Use `read_skill_resource` to read a skill resource.
        - Use `run_skill_script` to execute a skill script.
        - Pass the exact skill and script names advertised by the skill provider.
        - If a skill or script is not found, report the error. Do not search the project workspace.
        """;

    private async Task<AIContextProvider?> CreateSkillsProviderAsync(
        Agent agent,
        Project project,
        IReadOnlyList<PluginSkillReference> pluginSkills)
    {
        var skillIds = agent.AgentSkillRelations
            .Select(relation => relation.SkillId)
            .Concat(project.ProjectSkillRelations.Select(relation => relation.SkillId));
        var skills = await _agentAppService.ListSkillsAsync(skillIds);
        var classSkillRegistrations = skills
            .Select(skill => _skillRegistrations.GetValueOrDefault(skill.Id))
            .Where(registration => registration != null)
            .Cast<IAgentSkillRegistration>()
            .ToArray();
        var userSkillPaths = skills
            .Where(skill => !_skillRegistrations.ContainsKey(skill.Id))
            .Select(GetSkillAbsolutePath)
            .Where(Directory.Exists)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var userSkillNames = skills
            .Select(skill => skill.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pluginSkillPaths = new List<string>();
        var pluginSkillNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pluginSkill in pluginSkills)
        {
            if (userSkillNames.Contains(pluginSkill.SkillId))
            {
                _logger.LogWarning(
                    "User skill {SkillName} overrides plugin skill from plugin {PluginId}",
                    pluginSkill.SkillId,
                    pluginSkill.PluginId);
                continue;
            }

            if (!TryGetPluginSkillDirectory(pluginSkill.SkillFilePath, out var skillDirectory))
            {
                _logger.LogWarning(
                    "Plugin skill {SkillName} from plugin {PluginId} has no valid SKILL.md",
                    pluginSkill.SkillId,
                    pluginSkill.PluginId);
                continue;
            }

            if (!pluginSkillNames.Add(pluginSkill.SkillId))
            {
                _logger.LogWarning(
                    "Plugin skill {SkillName} from plugin {PluginId} conflicts with another plugin skill",
                    pluginSkill.SkillId,
                    pluginSkill.PluginId);
                continue;
            }

            pluginSkillPaths.Add(skillDirectory);
        }

        if (classSkillRegistrations.Length == 0 &&
            userSkillPaths.Length == 0 &&
            pluginSkillPaths.Count == 0)
        {
            if (agent.AgentSkillRelations.Count > 0 || project.ProjectSkillRelations.Count > 0 || pluginSkills.Count > 0)
            {
                _logger.LogWarning(
                    "Agent {AgentId} has skill references configured but no valid skill sources were found.",
                    agent.Id);
            }

            return null;
        }

        var builder = new AgentSkillsProviderBuilder()
            .UsePromptTemplate(SkillsInstructionPrompt);
        foreach (var registration in classSkillRegistrations)
        {
            builder.UseSkill(registration.Create(project.Id));
        }

        if (userSkillPaths.Length > 0)
        {
            builder.UseFileSkills(
                userSkillPaths,
                new AgentFileSkillsSourceOptions
                {
                    AllowedScriptExtensions = [.. LocalSkillScriptRunner.SupportedScriptExtensions],
                },
                LocalSkillScriptRunner.RunAsync);
        }

        if (pluginSkillPaths.Count > 0)
        {
            builder.UseFileSkills(
                pluginSkillPaths.Distinct(StringComparer.Ordinal),
                new AgentFileSkillsSourceOptions
                {
                    AllowedScriptExtensions = [],
                },
                RejectPluginSkillScriptAsync);
        }

        return builder.Build();
    }

    private string GetSkillAbsolutePath(Skill skill)
    {
        if (!string.IsNullOrWhiteSpace(skill.ContentPath))
        {
            var normalizedPath = skill.ContentPath.Replace('/', Path.DirectorySeparatorChar);
            return Path.Combine(_dataPaths.Root, normalizedPath);
        }

        return Path.Combine(_dataPaths.SkillsDirectory, skill.Name);
    }

    private static bool TryGetPluginSkillDirectory(string skillFilePath, out string skillDirectory)
    {
        skillDirectory = string.Empty;
        try
        {
            if (!string.Equals(
                    Path.GetFileName(skillFilePath),
                    "SKILL.md",
                    StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(skillFilePath))
            {
                return false;
            }

            skillDirectory = Path.GetDirectoryName(Path.GetFullPath(skillFilePath)) ?? string.Empty;
            return !string.IsNullOrWhiteSpace(skillDirectory);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static Task<object?> RejectPluginSkillScriptAsync(
        AgentFileSkill skill,
        AgentFileSkillScript script,
        System.Text.Json.JsonElement? arguments,
        IServiceProvider? serviceProvider,
        CancellationToken cancellationToken)
    {
        return Task.FromException<object?>(
            new InvalidOperationException("Plugin skill scripts are not trusted for execution."));
    }
}
