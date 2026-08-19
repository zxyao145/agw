using Agw.Skills.Application.Remote;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Skills.Execution;

public sealed class RemoteAgentSkill : AgentSkill
{
    private readonly Guid _skillId;
    private readonly IRemoteSkillContentResolver _contentResolver;

    public RemoteAgentSkill(Guid skillId, string name, string description, IRemoteSkillContentResolver contentResolver)
    {
        _skillId = skillId;
        _contentResolver = contentResolver;
        Frontmatter = new AgentSkillFrontmatter(name, description);
    }

    public override AgentSkillFrontmatter Frontmatter { get; }

    public override async ValueTask<string> GetContentAsync(CancellationToken cancellationToken = default)
    {
        var definition = await _contentResolver.ResolveAsync(_skillId, cancellationToken);
        var metadata =
            definition.Tags.Count == 0
                ? null
                : new AdditionalPropertiesDictionary { ["tags"] = definition.Tags.ToArray() };
        var skill = new AgentInlineSkill(
            definition.Name,
            definition.Description,
            definition.Instructions,
            metadata: metadata
        );
        return await skill.GetContentAsync(cancellationToken);
    }
}
