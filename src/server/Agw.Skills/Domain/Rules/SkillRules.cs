using System.Text.RegularExpressions;
using Agw.Shared.Data.Entities.Skills;
using Agw.Shared.Exceptions;

namespace Agw.Skills.Domain.Rules;

public static partial class SkillRules
{
    private const int MaxNameLength = 64;
    private const int MaxDescriptionLength = 1024;

    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$")]
    private static partial Regex SkillNameRegex();

    public static string GetContentPath(SkillKind kind, Guid skillId) =>
        kind == SkillKind.Local ? $"skills/{skillId:N}" : string.Empty;

    public static void Validate(string name, string description)
    {
        ValidateName(name);
        ValidateDescription(description);
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new AgwException(ErrorCodes.SkillNameRequired);
        }

        var trimmed = name.Trim();
        if (trimmed.Length > MaxNameLength)
        {
            throw new AgwException(
                ErrorCodes.SkillNameTooLong,
                $"Skill name must be {MaxNameLength} characters or fewer."
            );
        }

        if (!SkillNameRegex().IsMatch(trimmed))
        {
            throw new AgwException(
                ErrorCodes.SkillNameInvalidFormat,
                "Skill name must contain only lowercase letters, numbers, and single hyphens."
            );
        }
    }

    private static void ValidateDescription(string description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            throw new AgwException(ErrorCodes.SkillDescriptionRequired);
        }

        if (description.Trim().Length > MaxDescriptionLength)
        {
            throw new AgwException(
                ErrorCodes.SkillDescriptionTooLong,
                $"Skill description must be {MaxDescriptionLength} characters or fewer."
            );
        }
    }
}
