using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Exceptions;

namespace Agw.Projects.Domain.Behaviors;

public sealed class ProjectConversationBehavior
{
    public const string DefaultTitle = "New Chat";

    private const string PlaceholderTitle = "Untitled";
    private const int MaxTitleLength = 40;

    private readonly ProjectConversation _conversation;

    public ProjectConversationBehavior(ProjectConversation conversation)
    {
        _conversation = conversation;
    }

    /// <summary>
    /// <para>从消息文本派生会话标题，文本为空时回退到占位标题。</para>
    /// <para>Derives a conversation title from message text, falling back to the placeholder title when the text is blank.</para>
    /// </summary>
    public static string DeriveTitle(string? text, string fallback = DefaultTitle)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return fallback;
        }

        var firstLine = trimmed.Split(['\r', '\n'], 2)[0];
        return firstLine[..Math.Min(firstLine.Length, MaxTitleLength)];
    }

    public void SetInitialTitle(string? requestedTitle, string? input)
    {
        _conversation.Title = ResolveTitle(requestedTitle, input);
    }

    /// <summary>
    /// <para>当会话仍显示创建时的默认标题（"New Chat"）时，采纳由消息文本派生的标题。</para>
    /// <para>Adopts a title derived from message text while the conversation still shows the created-chat default title.</para>
    /// </summary>
    public void TryAdoptDerivedTitle(string? input)
    {
        var title = DeriveTitle(input);
        if (
            string.Equals(_conversation.Title, DefaultTitle, StringComparison.Ordinal)
            && !string.Equals(title, DefaultTitle, StringComparison.Ordinal)
        )
        {
            _conversation.Title = title;
        }
    }

    /// <summary>
    /// <para>当会话仍是实体默认标题（"Untitled"）时，采纳请求标题或由输入派生的标题。</para>
    /// <para>Adopts the requested or derived title while the conversation still carries the entity default title.</para>
    /// </summary>
    public void TryAdoptRequestedTitle(string? requestedTitle, string? input)
    {
        if (!string.Equals(_conversation.Title, PlaceholderTitle, StringComparison.Ordinal))
        {
            return;
        }

        _conversation.Title = ResolveTitle(requestedTitle, input);
    }

    public bool TryRename(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        _conversation.Title = title.Trim();
        return true;
    }

    public void TryBindJob(Guid? jobId)
    {
        if (_conversation.JobId == null && jobId.HasValue)
        {
            _conversation.JobId = jobId.Value;
        }
    }

    public void StampUpdate(string user, DateTimeOffset now)
    {
        _conversation.UpdateBy = user;
        _conversation.UpdateTime = now;
    }

    public void EnsureIdentity(Guid conversationId)
    {
        if (_conversation.Id != conversationId)
        {
            throw new AgwException(
                ErrorCodes.InvalidParam,
                "The supplied conversation identity does not match the execution context."
            );
        }
    }

    public void EnsureExecutionContext(Guid projectId, string contextId)
    {
        var existingContextId = ContextIdUtil.NormalizeContextId(_conversation.ContextId);
        if (
            _conversation.ProjectId != projectId
            || !string.Equals(existingContextId, contextId, StringComparison.Ordinal)
        )
        {
            throw new AgwException(
                ErrorCodes.InvalidParam,
                "The supplied conversation identity does not match the execution context."
            );
        }

        _conversation.ContextId = existingContextId;
    }

    private static string ResolveTitle(string? requestedTitle, string? input) =>
        string.IsNullOrWhiteSpace(requestedTitle) ? DeriveTitle(input) : requestedTitle.Trim();
}
