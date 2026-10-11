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

    private static readonly Guid SessionAgentId = Guid.CreateVersion7();
    private static readonly DateTimeOffset SessionTime = new(2026, 10, 9, 2, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ActivateProviderSession_EmptyBindings_CreatesActiveBinding()
    {
        var conversation = new ProjectConversation { Id = Guid.CreateVersion7(), CreateBy = "owner" };
        var behavior = new ProjectConversationBehavior(conversation);

        Assert.Null(behavior.PrepareProviderSessionActivation(SessionAgentId, "codex", "A", "owner", SessionTime));
        var binding = behavior.ActivateProviderSession(SessionAgentId, "codex", "A", "owner", SessionTime);

        Assert.Same(binding, Assert.Single(conversation.Bindings));
        Assert.True(binding.IsActive);
        Assert.Equal(conversation.Id, binding.ProjectConversationId);
        Assert.Equal(SessionAgentId, binding.AgentId);
        Assert.Equal("codex", binding.ExternalAgentName);
        Assert.Equal("A", binding.ProviderSessionId);
        Assert.Equal("owner", binding.CreateBy);
        Assert.Equal(SessionTime, binding.CreateTime);
        Assert.Null(binding.UpdateTime);
    }

    [Fact]
    public void ActivateProviderSession_NewSession_PreservesHistory()
    {
        var active = CreateBinding("A", isActive: true);
        var conversation = CreateConversation(active);
        var behavior = new ProjectConversationBehavior(conversation);
        var now = SessionTime.AddHours(1);

        Assert.Null(behavior.PrepareProviderSessionActivation(SessionAgentId, "codex", "B", "owner", now));
        var created = behavior.ActivateProviderSession(SessionAgentId, "codex", "B", "owner", now);

        Assert.Equal(2, conversation.Bindings.Count);
        Assert.False(active.IsActive);
        Assert.Equal("owner", active.UpdateBy);
        Assert.Equal(now, active.UpdateTime);
        Assert.True(created.IsActive);
        Assert.Equal("B", created.ProviderSessionId);
    }

    [Fact]
    public void ActivateProviderSession_CurrentSession_IsIdempotent()
    {
        var active = CreateBinding("A", isActive: true);
        var conversation = CreateConversation(active);

        var current = new ProjectConversationBehavior(conversation).PrepareProviderSessionActivation(
            SessionAgentId,
            "codex",
            "A",
            "owner",
            SessionTime.AddHours(1)
        );

        Assert.Same(active, current);
        Assert.Same(active, Assert.Single(conversation.Bindings));
        Assert.True(active.IsActive);
        Assert.Null(active.UpdateBy);
        Assert.Null(active.UpdateTime);
    }

    [Fact]
    public void ActivateProviderSession_ArchivedSession_RejectsWrite()
    {
        var archived = CreateBinding("A", isActive: false);
        var active = CreateBinding("B", isActive: true);
        var conversation = CreateConversation(archived, active);

        var exception = Assert.Throws<AgwException>(() =>
            new ProjectConversationBehavior(conversation).PrepareProviderSessionActivation(
                SessionAgentId,
                "codex",
                "A",
                "owner",
                SessionTime.AddHours(1)
            )
        );

        Assert.Equal(ErrorCodes.ConversationSessionConflict.Code, exception.Code);
        Assert.False(archived.IsActive);
        Assert.True(active.IsActive);
        Assert.Null(active.UpdateTime);
    }

    [Fact]
    public void ActivateProviderSession_GroupStillActive_RejectsSecondActiveRecord()
    {
        var active = CreateBinding("A", isActive: true);
        var conversation = CreateConversation(active);

        var exception = Assert.Throws<AgwException>(() =>
            new ProjectConversationBehavior(conversation).ActivateProviderSession(
                SessionAgentId,
                "codex",
                "B",
                "owner",
                SessionTime
            )
        );

        Assert.Equal(ErrorCodes.ConversationSessionConflict.Code, exception.Code);
        Assert.Same(active, Assert.Single(conversation.Bindings));
    }

    [Fact]
    public void PrepareProviderSessionActivation_OtherGroups_AreUnchanged()
    {
        var otherAgent = CreateBinding("A", isActive: true, agentId: Guid.CreateVersion7());
        var otherName = CreateBinding("A", isActive: true, externalAgentName: "pi");
        var conversation = CreateConversation(otherAgent, otherName);

        Assert.Null(
            new ProjectConversationBehavior(conversation).PrepareProviderSessionActivation(
                SessionAgentId,
                "codex",
                "A",
                "owner",
                SessionTime
            )
        );

        Assert.True(otherAgent.IsActive);
        Assert.True(otherName.IsActive);
    }

    [Fact]
    public void ArchiveProviderSession_ActiveBinding_ArchivesOnlyTarget()
    {
        var target = CreateBinding("A", isActive: true);
        var other = CreateBinding("B", isActive: true, externalAgentName: "pi");
        var conversation = CreateConversation(target, other);
        var now = SessionTime.AddHours(1);

        Assert.True(new ProjectConversationBehavior(conversation).ArchiveProviderSession(target.Id, "owner", now));

        Assert.False(target.IsActive);
        Assert.Equal("owner", target.UpdateBy);
        Assert.Equal(now, target.UpdateTime);
        Assert.True(other.IsActive);
        Assert.Null(other.UpdateTime);
    }

    [Fact]
    public void ArchiveProviderSession_ArchivedBinding_IsIdempotent()
    {
        var target = CreateBinding("A", isActive: false);
        target.UpdateBy = "owner";
        target.UpdateTime = SessionTime;
        var conversation = CreateConversation(target);

        Assert.False(
            new ProjectConversationBehavior(conversation).ArchiveProviderSession(
                target.Id,
                "someone-else",
                SessionTime.AddHours(1)
            )
        );

        Assert.False(target.IsActive);
        Assert.Equal("owner", target.UpdateBy);
        Assert.Equal(SessionTime, target.UpdateTime);
    }

    [Fact]
    public void ArchiveProviderSession_MissingBinding_ThrowsResourceNotFound()
    {
        var conversation = CreateConversation(CreateBinding("A", isActive: true));

        var exception = Assert.Throws<AgwException>(() =>
            new ProjectConversationBehavior(conversation).ArchiveProviderSession(
                Guid.CreateVersion7(),
                "owner",
                SessionTime
            )
        );

        Assert.Equal(ErrorCodes.ResourceNotFound.Code, exception.Code);
    }

    private static ProjectConversation CreateConversation(params ProjectConversationBinding[] bindings)
    {
        var conversation = new ProjectConversation { Id = Guid.CreateVersion7(), CreateBy = "owner" };
        foreach (var binding in bindings)
        {
            binding.ProjectConversationId = conversation.Id;
            conversation.Bindings.Add(binding);
        }

        return conversation;
    }

    private static ProjectConversationBinding CreateBinding(
        string providerSessionId,
        bool isActive,
        Guid? agentId = null,
        string externalAgentName = "codex"
    ) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            AgentId = agentId ?? SessionAgentId,
            ExternalAgentName = externalAgentName,
            ProviderSessionId = providerSessionId,
            IsActive = isActive,
            CreateBy = "owner",
            CreateTime = SessionTime,
        };
}
