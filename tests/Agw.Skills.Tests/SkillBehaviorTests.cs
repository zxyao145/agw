using Agw.Shared.Data.Entities.Skills;
using Agw.Shared.Exceptions;
using Agw.Skills.Contracts;
using Agw.Skills.Domain.Behaviors;

namespace Agw.Skills.Tests;

public class SkillBehaviorTests
{
    [Theory]
    [InlineData("Expense-Report")]
    [InlineData("expense_report")]
    [InlineData("-expense")]
    [InlineData("expense-")]
    [InlineData("expense--report")]
    public void DefineLocal_InvalidSkillName_ThrowsAgwException(string name)
    {
        var skill = new Skill { Kind = SkillKind.Local };

        var exception = Assert.Throws<AgwException>(() => new SkillBehavior(skill).DefineLocal(name, "desc"));

        Assert.Equal(ErrorCodes.SkillNameInvalidFormat.Code, exception.Code);
    }

    [Theory]
    [InlineData("", "description", "name-required")]
    [InlineData("valid-name", " ", "description-required")]
    [InlineData("long-name", "description", "name-too-long")]
    [InlineData("valid-name", "long-description", "description-too-long")]
    public void DefineLocal_MissingOrOversizedValues_PreservesErrorCodes(string name, string description, string error)
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
        var skill = new Skill { Kind = SkillKind.Local };

        var exception = Assert.Throws<AgwException>(() => new SkillBehavior(skill).DefineLocal(name, description));

        Assert.Equal(expected.Code, exception.Code);
    }

    [Fact]
    public void DefineLocal_ValidDefinition_StoresTrimmedValuesUnderStableIdFolder()
    {
        var skill = new Skill { Kind = SkillKind.Local, RemoteUrl = "https://example.test/skill" };

        new SkillBehavior(skill).DefineLocal("  expense-report  ", "  Reports expenses.  ");

        Assert.NotEqual(Guid.Empty, skill.Id);
        Assert.Equal("expense-report", skill.Name);
        Assert.Equal("Reports expenses.", skill.Description);
        Assert.Equal($"skills/{skill.Id:N}", skill.ContentPath);
        Assert.Null(skill.RemoteUrl);
    }

    [Fact]
    public void DefineRemote_ValidDefinition_StoresRemoteUrlWithoutContentPath()
    {
        var skill = new Skill { Kind = SkillKind.Remote };

        new SkillBehavior(skill).DefineRemote("https://example.test/skill", "remote-skill", "Remote.");

        Assert.Equal("https://example.test/skill", skill.RemoteUrl);
        Assert.Equal(string.Empty, skill.ContentPath);
    }

    [Theory]
    [InlineData(SkillKind.Local, false, null, "SkillArchiveCannotBeEmpty")]
    [InlineData(SkillKind.Local, true, "https://example.test/skill", "SkillKindInvalid")]
    [InlineData(SkillKind.Remote, true, null, "RemoteSkillArchiveNotAllowed")]
    [InlineData(SkillKind.BuiltIn, false, null, "SkillKindInvalid")]
    public void EnsureCreatable_InvalidInput_ThrowsKindSpecificError(
        SkillKind kind,
        bool archiveSupplied,
        string? remoteUrl,
        string error
    )
    {
        var expected = error switch
        {
            "SkillArchiveCannotBeEmpty" => ErrorCodes.SkillArchiveCannotBeEmpty,
            "RemoteSkillArchiveNotAllowed" => ErrorCodes.RemoteSkillArchiveNotAllowed,
            _ => ErrorCodes.SkillKindInvalid,
        };
        var skill = new Skill { Kind = kind, RemoteUrl = remoteUrl };

        var exception = Assert.Throws<AgwException>(() => new SkillBehavior(skill).EnsureCreatable(archiveSupplied));

        Assert.Equal(expected.Code, exception.Code);
    }

    [Fact]
    public void EnsureUpdatable_LocalRenameWithoutArchive_ThrowsNameUpdateRequiresArchive()
    {
        var skill = new Skill { Kind = SkillKind.Local, Name = "expense-report" };

        var exception = Assert.Throws<AgwException>(() =>
            new SkillBehavior(skill).EnsureUpdatable("travel-report", archiveSupplied: false, remoteUrl: null)
        );

        Assert.Equal(ErrorCodes.SkillNameUpdateRequiresArchive.Code, exception.Code);
    }

    [Fact]
    public void EnsureUpdatable_LocalSameNameWithoutArchive_Succeeds()
    {
        var skill = new Skill { Kind = SkillKind.Local, Name = "expense-report" };

        new SkillBehavior(skill).EnsureUpdatable("  expense-report ", archiveSupplied: false, remoteUrl: null);
    }

    [Theory]
    [InlineData(SkillKind.BuiltIn, false)]
    [InlineData(SkillKind.Local, true)]
    public void EnsureMutable_BuiltInSkill_ThrowsImmutable(SkillKind kind, bool isRegisteredBuiltIn)
    {
        var skill = new Skill { Kind = kind };

        var exception = Assert.Throws<AgwException>(() => new SkillBehavior(skill).EnsureMutable(isRegisteredBuiltIn));

        Assert.Equal(ErrorCodes.BuiltInSkillImmutable.Code, exception.Code);
    }

    [Fact]
    public void EnsureRemoteIdentity_RenamedDefinition_ThrowsIdentityChanged()
    {
        var skill = new Skill { Kind = SkillKind.Remote, Name = "remote-skill" };

        var exception = Assert.Throws<AgwException>(() =>
            new SkillBehavior(skill).EnsureRemoteIdentity("renamed-skill")
        );

        Assert.Equal(ErrorCodes.RemoteSkillIdentityChanged.Code, exception.Code);
    }

    [Theory]
    [InlineData(SkillKind.Local, "https://example.test/skill")]
    [InlineData(SkillKind.Remote, " ")]
    public void EnsureRemote_NotConfiguredRemoteSkill_ThrowsConfigurationInvalid(SkillKind kind, string remoteUrl)
    {
        var skill = new Skill { Kind = kind, RemoteUrl = remoteUrl };

        var exception = Assert.Throws<AgwException>(() => new SkillBehavior(skill).EnsureRemote());

        Assert.Equal(ErrorCodes.RemoteSkillConfigurationInvalid.Code, exception.Code);
    }
}
