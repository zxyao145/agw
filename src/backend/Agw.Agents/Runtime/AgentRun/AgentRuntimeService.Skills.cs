using Agw.Shared.Data.Entities.Skills;

using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;

namespace Agw.Agents.Runtime.AgentRun;

public partial class AgentRuntimeService
{
    private async Task<AIContextProvider?> CreateSkillsProviderAsync(Guid agentId)
    {
        var skills = await _agentAppService.ListSkillsByAgentAsync(agentId);
        var skillPaths = skills
            .Select(GetSkillAbsolutePath)
            .Where(Directory.Exists)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (skillPaths.Length == 0)
        {
            _logger.LogWarning(
                "Agent {AgentId} has skill relations configured but no extracted skill directories were found.",
                agentId);
            return null;
        }

        return new AgentSkillsProvider(skillPaths: skillPaths);
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
