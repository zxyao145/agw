using Agw.Domain.Entities;
using System.Text.RegularExpressions;

namespace Agw.Domain.Services.Skills;

public partial class SkillDomainService
{
    private const int MaxNameLength = 64;
    private const int MaxDescriptionLength = 1024;

    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$")]
    private static partial Regex SkillNameRegex();

    public void PrepareForCreate(Skill skill, string user)
    {
        ArgumentNullException.ThrowIfNull(skill);

        Validate(skill.Name, skill.Description);
        skill.Id = skill.Id == Guid.Empty ? Guid.NewGuid() : skill.Id;
        skill.CreateBy = user;
        skill.CreateTime = DateTime.UtcNow;
        skill.ContentPath = BuildContentPath(skill.Name);
    }

    public void ApplyUpdate(Skill skill, string name, string description, string user)
    {
        ArgumentNullException.ThrowIfNull(skill);

        Validate(name, description);
        skill.Name = name.Trim();
        skill.Description = description.Trim();
        skill.ContentPath = BuildContentPath(skill.Name);
        skill.UpdateBy = user;
        skill.UpdateTime = DateTime.UtcNow;
    }

    public IReadOnlyList<Guid> NormalizeAgentIds(IEnumerable<Guid>? agentIds)
    {
        return (agentIds ?? [])
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
    }

    public string BuildContentPath(string skillName)
    {
        ValidateName(skillName);
        return $"skills/{skillName}";
    }

    public void Validate(string name, string description)
    {
        ValidateName(name);
        ValidateDescription(description);
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException("Skill name is required.");
        }

        var trimmed = name.Trim();
        if (trimmed.Length > MaxNameLength)
        {
            throw new InvalidOperationException($"Skill name must be {MaxNameLength} characters or fewer.");
        }

        if (!SkillNameRegex().IsMatch(trimmed))
        {
            throw new InvalidOperationException("Skill name must contain only lowercase letters, numbers, and single hyphens.");
        }
    }

    private static void ValidateDescription(string description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            throw new InvalidOperationException("Skill description is required.");
        }

        if (description.Trim().Length > MaxDescriptionLength)
        {
            throw new InvalidOperationException($"Skill description must be {MaxDescriptionLength} characters or fewer.");
        }
    }
}
