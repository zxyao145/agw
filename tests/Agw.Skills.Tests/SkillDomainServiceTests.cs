using Agw.Domain.Entities;
using Agw.Domain.Services.Skills;

namespace Agw.Skills.Tests;

public class SkillDomainServiceTests
{
    private readonly SkillDomainService _service = new();

    [Fact]
    public void PrepareForCreate_AssignsMetadataAndContentPath()
    {
        var before = DateTime.UtcNow;
        var skill = new Skill
        {
            Name = "expense-report",
            Description = "Validate expense submissions.",
        };

        _service.PrepareForCreate(skill, "tester");

        Assert.NotEqual(Guid.Empty, skill.Id);
        Assert.Equal("skills/expense-report", skill.ContentPath);
        Assert.Equal("tester", skill.CreateBy);
        Assert.InRange(skill.CreateTime, before, DateTime.UtcNow);
    }

    [Theory]
    [InlineData("Expense-Report")]
    [InlineData("expense_report")]
    [InlineData("-expense")]
    [InlineData("expense-")]
    [InlineData("expense--report")]
    public void Validate_InvalidSkillName_ThrowsInvalidOperationException(string name)
    {
        Assert.Throws<InvalidOperationException>(() => _service.Validate(name, "desc"));
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
