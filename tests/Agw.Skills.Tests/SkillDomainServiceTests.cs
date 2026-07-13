using Agw.Domain.Services.Skills;
using Agw.Shared.Data.Entities.Skills;
using Agw.Shared.Exceptions;
using Agw.Testing;

namespace Agw.Skills.Tests;

public class SkillDomainServiceTests
{
    private static readonly DateTimeOffset UtcNow = new(2026, 7, 13, 8, 0, 0, TimeSpan.Zero);
    private readonly SkillDomainService _service = new(new TestTimeProvider(UtcNow));

    [Fact]
    public void PrepareForCreate_AssignsMetadataAndContentPath()
    {
        var skill = new Skill
        {
            Name = "expense-report",
            Description = "Validate expense submissions.",
        };

        _service.PrepareForCreate(skill, "tester");

        Assert.NotEqual(Guid.Empty, skill.Id);
        Assert.Equal("skills/expense-report", skill.ContentPath);
        Assert.Equal("tester", skill.CreateBy);
        Assert.Equal(UtcNow, skill.CreateTime);
    }

    [Theory]
    [InlineData("Expense-Report")]
    [InlineData("expense_report")]
    [InlineData("-expense")]
    [InlineData("expense-")]
    [InlineData("expense--report")]
    public void Validate_InvalidSkillName_ThrowsAgwException(string name)
    {
        var exception = Assert.Throws<AgwException>(() => _service.Validate(name, "desc"));
        Assert.Equal(ErrorCodes.SkillNameInvalidFormat.Code, exception.Code);
    }

    [Fact]
    public void NormalizeAgentIds_RemovesEmptyValuesAndDuplicates()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        var result = _service.NormalizeAgentIds([Guid.Empty, first, second, first]);

        Assert.Equal([first, second], result);
    }
}
