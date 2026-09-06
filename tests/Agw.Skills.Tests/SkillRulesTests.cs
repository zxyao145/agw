using Agw.Shared.Data.Entities.Skills;
using Agw.Shared.Exceptions;
using Agw.Skills.Domain.Rules;

namespace Agw.Skills.Tests;

public class SkillRulesTests
{
    [Theory]
    [InlineData("Expense-Report")]
    [InlineData("expense_report")]
    [InlineData("-expense")]
    [InlineData("expense-")]
    [InlineData("expense--report")]
    public void Validate_InvalidSkillName_ThrowsAgwException(string name)
    {
        var exception = Assert.Throws<AgwException>(() => SkillRules.Validate(name, "desc"));

        Assert.Equal(ErrorCodes.SkillNameInvalidFormat.Code, exception.Code);
    }

    [Theory]
    [InlineData("", "description", "name-required")]
    [InlineData("valid-name", " ", "description-required")]
    [InlineData("long-name", "description", "name-too-long")]
    [InlineData("valid-name", "long-description", "description-too-long")]
    public void Validate_MissingOrOversizedValues_PreservesErrorCodes(string name, string description, string error)
    {
        if (error == "name-too-long")
            name = new string('a', 65);
        if (error == "description-too-long")
            description = new string('a', 1025);
        var expected = error switch
        {
            "name-required" => ErrorCodes.SkillNameRequired,
            "description-required" => ErrorCodes.SkillDescriptionRequired,
            "name-too-long" => ErrorCodes.SkillNameTooLong,
            _ => ErrorCodes.SkillDescriptionTooLong,
        };

        var exception = Assert.Throws<AgwException>(() => SkillRules.Validate(name, description));

        Assert.Equal(expected.Code, exception.Code);
    }

    [Theory]
    [InlineData(SkillKind.Local)]
    [InlineData(SkillKind.Remote)]
    [InlineData(SkillKind.BuiltIn)]
    public void GetContentPath_UsesStableIdOnlyForLocalContent(SkillKind kind)
    {
        var id = Guid.CreateVersion7();

        var path = SkillRules.GetContentPath(kind, id);

        Assert.Equal(kind == SkillKind.Local ? $"skills/{id:N}" : string.Empty, path);
    }
}
