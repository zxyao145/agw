using Agw.Agents.Execution.Agents.Skills;
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

    private async Task<AIContextProvider?> CreateSkillsProviderAsync(Agent agent, Project project)
    {
        var skillIds = agent.AgentSkillRelations
            .Select(relation => relation.SkillId)
            .Concat(project.ProjectSkillRelations.Select(relation => relation.SkillId));
        var skills = await _agentAppService.ListSkillsAsync(skillIds);
        var skillPaths = skills
            .Select(GetSkillAbsolutePath)
            .Where(Directory.Exists)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (skillPaths.Length == 0)
        {
            _logger.LogWarning(
                "Agent {AgentId} has skill relations configured but no extracted skill directories were found.",
                agent.Id);
            return null;
        }

        return new AgentSkillsProvider(
            skillPaths: skillPaths,
            scriptRunner: LocalSkillScriptRunner.RunAsync,
            fileOptions: new AgentFileSkillsSourceOptions
            {
                AllowedScriptExtensions = [.. LocalSkillScriptRunner.SupportedScriptExtensions],
            },
            options: new AgentSkillsProviderOptions
            {
                SkillsInstructionPrompt = SkillsInstructionPrompt,
            });
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
}
