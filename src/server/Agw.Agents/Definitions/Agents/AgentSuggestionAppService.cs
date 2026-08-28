using Agw.Agents.Definitions.Contracts;
using Agw.Agents.ExternalAgents;
using Agw.Auth.Contracts;
using Agw.Projects.Contracts.Runtime;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Skills;
using Agw.Shared.Data.Repositories;
using Agw.Shared.Exceptions;
using Agw.Tools;

namespace Agw.Agents.Definitions.Agents;

public class AgentSuggestionAppService
{
    private readonly IRepository<Agent> _agentRepository;
    private readonly IProjectRuntimeFacade _projects;
    private readonly IRepository<Skill> _skillRepository;
    private readonly ToolRegistryService _toolRegistryService;
    private readonly IUserInfoService _userInfoService;

    public AgentSuggestionAppService(
        IRepository<Agent> agentRepository,
        IProjectRuntimeFacade projects,
        IRepository<Skill> skillRepository,
        ToolRegistryService toolRegistryService,
        IUserInfoService userInfoService
    )
    {
        _agentRepository = agentRepository;
        _projects = projects;
        _skillRepository = skillRepository;
        _toolRegistryService = toolRegistryService;
        _userInfoService = userInfoService;
    }

    public async Task<AgentSuggestionsResponse> GetSuggestionsAsync(Guid? projectId, Guid agentId)
    {
        var ownerUserId = ResolveOwnerUserId();
        var agents = await _agentRepository.ListAsync(
            agent => agent.Id == agentId && agent.CreateBy == ownerUserId,
            null,
            agent => agent.AgentSkillRelations
        );
        var agent = agents.FirstOrDefault() ?? throw new AgwException(ErrorCodes.AgentNotFound);

        ProjectRuntimeSnapshot? project = null;
        if (projectId.HasValue)
        {
            project = await _projects.GetForCurrentUserAsync(projectId.Value).ConfigureAwait(false);
            if (project == null)
            {
                throw new AgwException(ErrorCodes.ResourceNotFound, $"Project '{projectId.Value}' was not found.");
            }
        }

        if (agent.Type == AgentType.External)
        {
            var mode = string.Equals(agent.Name, AgentNames.ClaudeCode, StringComparison.OrdinalIgnoreCase)
                ? AgentSuggestionMode.ClaudeCode
                : AgentSuggestionMode.Unsupported;
            return new AgentSuggestionsResponse(mode, []);
        }

        var suggestions = new List<AgentSuggestionResponse>();
        IEnumerable<Guid> relatedSkillIds = agent.AgentSkillRelations.Select(relation => relation.SkillId);
        if (project != null)
        {
            relatedSkillIds = relatedSkillIds.Concat(project.SkillIds);
        }

        var skillIds = relatedSkillIds.Where(id => id != Guid.Empty).Distinct().ToArray();
        if (skillIds.Length > 0)
        {
            var skills = await _skillRepository.ListAsync(skill =>
                skillIds.Contains(skill.Id) && (skill.Kind == SkillKind.BuiltIn || skill.CreateBy == ownerUserId)
            );
            suggestions.AddRange(
                skills
                    .Where(skill => !string.IsNullOrWhiteSpace(skill.Name))
                    .Select(skill => new AgentSuggestionResponse(
                        ToCommandText(skill.Name),
                        JoinDescription("Skill", skill.Description),
                        AgentSuggestionKind.Skill
                    ))
            );
        }

        var resolvedToolValues = ToolValueResolution.Resolve(agent.Tools, project?.Tools);
        foreach (var definition in resolvedToolValues.Tools)
        {
            var toolName = definition.GetDefinitionName();
            var tool = _toolRegistryService.GetTool(toolName);
            if (tool == null)
            {
                continue;
            }

            suggestions.Add(
                new AgentSuggestionResponse(
                    ToCommandText(tool.Name),
                    JoinDescription("Tool", tool.Category, tool.Description),
                    AgentSuggestionKind.Tool
                )
            );
        }

        foreach (var definition in resolvedToolValues.ToolBlocks)
        {
            var toolBlock = _toolRegistryService.GetTool(definition.GetDefinitionName());
            if (toolBlock == null)
            {
                continue;
            }

            suggestions.AddRange(
                toolBlock.MemberToolNames.Select(memberToolName => new AgentSuggestionResponse(
                    ToCommandText(memberToolName),
                    JoinDescription("Tool", toolBlock.DisplayName, toolBlock.Description),
                    AgentSuggestionKind.Tool
                ))
            );
        }

        return new AgentSuggestionsResponse(
            AgentSuggestionMode.System,
            suggestions
                .OrderBy(suggestion => suggestion.Text, StringComparer.OrdinalIgnoreCase)
                .ThenBy(suggestion => suggestion.Kind)
                .ToArray()
        );
    }

    private static string ToCommandText(string name)
    {
        return $"/{name.Trim().TrimStart('/')}";
    }

    private static string JoinDescription(params string?[] parts)
    {
        return string.Join(" · ", parts.Where(part => !string.IsNullOrWhiteSpace(part)).Select(part => part!.Trim()));
    }

    private string ResolveOwnerUserId() => _userInfoService.RequiredUserId;
}
