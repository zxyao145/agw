using Agw.Projects.Domain.Behaviors;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Exceptions;

namespace Agw.Projects.Tests;

public class ProjectConversationBehaviorTests
{
    [Theory]
    [InlineData("  this is a chat title  ")]
    [InlineData("  this is a chat title\nsecond line  ")]
    [InlineData("  this is a chat title\r\nsecond line  ")]
    [InlineData("  this is a chat title\rsecond line  ")]
    public void DeriveTitle_PaddedInput_ReturnsTrimmedFirstLine(string input)
    {
        var title = ProjectConversationBehavior.DeriveTitle(input);

        Assert.Equal("this is a chat title", title);
    }

    [Fact]
    public void DeriveTitle_LongInput_KeepsFirstFortyCharacters()
    {
        var title = ProjectConversationBehavior.DeriveTitle(new string('a', 100) + "\nsecond line");

        Assert.Equal(new string('a', 40), title);
    }

    [Fact]
    public void DeriveTitle_BlankInput_ReturnsDefaultTitle()
    {
        var title = ProjectConversationBehavior.DeriveTitle("   ");

        Assert.Equal(ProjectConversationBehavior.DefaultTitle, title);
    }

    [Fact]
    public void SetInitialTitle_RequestedTitle_UsesTrimmedRequestedTitle()
    {
        var conversation = new ProjectConversation();

        new ProjectConversationBehavior(conversation).SetInitialTitle("  Requested  ", "input text");

        Assert.Equal("Requested", conversation.Title);
    }

    [Fact]
    public void SetInitialTitle_BlankRequestedTitle_DerivesTitleFromInput()
    {
        var conversation = new ProjectConversation();

        new ProjectConversationBehavior(conversation).SetInitialTitle(" ", "  input text  ");

        Assert.Equal("input text", conversation.Title);
    }

    [Fact]
    public void TryAdoptDerivedTitle_DefaultTitle_AdoptsTitleFromInput()
    {
        var conversation = new ProjectConversation { Title = ProjectConversationBehavior.DefaultTitle };

        new ProjectConversationBehavior(conversation).TryAdoptDerivedTitle("first message");

        Assert.Equal("first message", conversation.Title);
    }

    [Fact]
    public void TryAdoptDerivedTitle_RenamedConversation_KeepsTitle()
    {
        var conversation = new ProjectConversation { Title = "Renamed" };

        new ProjectConversationBehavior(conversation).TryAdoptDerivedTitle("first message");

        Assert.Equal("Renamed", conversation.Title);
    }

    [Fact]
    public void TryAdoptRequestedTitle_PlaceholderTitle_AdoptsRequestedTitle()
    {
        var conversation = new ProjectConversation();

        new ProjectConversationBehavior(conversation).TryAdoptRequestedTitle("Requested", "input text");

        Assert.Equal("Requested", conversation.Title);
    }

    [Fact]
    public void TryAdoptRequestedTitle_DerivedTitle_KeepsTitle()
    {
        var conversation = new ProjectConversation { Title = ProjectConversationBehavior.DefaultTitle };

        new ProjectConversationBehavior(conversation).TryAdoptRequestedTitle("Requested", "input text");

        Assert.Equal(ProjectConversationBehavior.DefaultTitle, conversation.Title);
    }

    [Fact]
    public void TryRename_BlankTitle_ReturnsFalseWithoutChange()
    {
        var conversation = new ProjectConversation { Title = "Original" };

        var renamed = new ProjectConversationBehavior(conversation).TryRename("  ");

        Assert.False(renamed);
        Assert.Equal("Original", conversation.Title);
    }

    [Fact]
    public void TryRename_PaddedTitle_StoresTrimmedTitle()
    {
        var conversation = new ProjectConversation { Title = "Original" };

        var renamed = new ProjectConversationBehavior(conversation).TryRename("  Renamed  ");

        Assert.True(renamed);
        Assert.Equal("Renamed", conversation.Title);
    }

    [Fact]
    public void TryBindJob_ConversationAlreadyBound_KeepsOriginalJob()
    {
        var originalJobId = Guid.CreateVersion7();
        var conversation = new ProjectConversation { JobId = originalJobId };

        new ProjectConversationBehavior(conversation).TryBindJob(Guid.CreateVersion7());

        Assert.Equal(originalJobId, conversation.JobId);
    }

    [Fact]
    public void EnsureExecutionContext_MatchingContext_NormalizesStoredContextId()
    {
        var projectId = Guid.CreateVersion7();
        var contextGuid = Guid.CreateVersion7();
        var conversation = new ProjectConversation
        {
            ProjectId = projectId,
            ContextId = contextGuid.ToString("N").ToUpperInvariant(),
        };

        new ProjectConversationBehavior(conversation).EnsureExecutionContext(
            projectId,
            ContextIdUtil.NormalizeContextId(contextGuid.ToString())
        );

        Assert.Equal(ContextIdUtil.NormalizeContextId(contextGuid.ToString()), conversation.ContextId);
    }

    [Fact]
    public void EnsureExecutionContext_DifferentProject_ThrowsInvalidParam()
    {
        var conversation = new ProjectConversation { ProjectId = Guid.CreateVersion7(), ContextId = "context" };

        var exception = Assert.Throws<AgwException>(() =>
            new ProjectConversationBehavior(conversation).EnsureExecutionContext(Guid.CreateVersion7(), "context")
        );

        Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code);
    }

    [Fact]
    public void EnsureIdentity_DifferentConversationId_ThrowsInvalidParam()
    {
        var conversation = new ProjectConversation { Id = Guid.CreateVersion7() };

        var exception = Assert.Throws<AgwException>(() =>
            new ProjectConversationBehavior(conversation).EnsureIdentity(Guid.CreateVersion7())
        );

        Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code);
    }
}
