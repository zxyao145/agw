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

    /// <summary>
    /// 校验对话属于该项目；调用方提供 context ID 时还要与保存值一致。
    /// Ensures the conversation belongs to the project; a context ID supplied by the caller must also match the saved one.
    /// </summary>
    public void EnsureExecutionContext(Guid projectId, string? contextId)
    {
        var existingContextId = ContextIdUtil.NormalizeContextId(_conversation.ContextId);
        if (
            _conversation.ProjectId != projectId
            || (contextId != null && !string.Equals(existingContextId, contextId, StringComparison.Ordinal))
        )
        {
            throw new AgwException(
                ErrorCodes.InvalidParam,
                "The supplied conversation identity does not match the execution context."
            );
        }

        _conversation.ContextId = existingContextId;
    }

    /// <summary>
    /// <para>开始在绑定组中启用 provider session：当前生效记录已经使用该 ID 时返回该记录；ID 属于已归档记录时拒绝；否则停用当前生效记录并返回 null，调用方保存后再调用 <see cref="ActivateProviderSession"/>。</para>
    /// <para>Starts enabling a provider session in a binding group: returns the active record when it already uses the ID; rejects an ID of an archived record; otherwise archives the active record and returns null, after which the caller saves and calls <see cref="ActivateProviderSession"/>.</para>
    /// </summary>
    public ProjectConversationBinding? PrepareProviderSessionActivation(
        Guid agentId,
        string externalAgentName,
        string providerSessionId,
        string user,
        DateTimeOffset now
    )
    {
        var group = GetBindingGroup(agentId, externalAgentName);
        var active = group.SingleOrDefault(binding => binding.IsActive);
        if (active != null && string.Equals(active.ProviderSessionId, providerSessionId, StringComparison.Ordinal))
        {
            return active;
        }

        EnsureProviderSessionUnused(group, providerSessionId);
        if (active != null)
        {
            Archive(active, user, now);
        }

        return null;
    }

    /// <summary>
    /// <para>在没有生效记录的绑定组中创建并启用新的 provider session 记录。</para>
    /// <para>Creates and enables a new provider session record in a binding group that has no active record.</para>
    /// </summary>
    public ProjectConversationBinding ActivateProviderSession(
        Guid agentId,
        string externalAgentName,
        string providerSessionId,
        string user,
        DateTimeOffset now
    )
    {
        var group = GetBindingGroup(agentId, externalAgentName);
        if (group.Any(binding => binding.IsActive))
        {
            throw new AgwException(
                ErrorCodes.ConversationSessionConflict,
                "The binding group already has an active provider session."
            );
        }

        EnsureProviderSessionUnused(group, providerSessionId);
        // 记录 ID 留空，由持久化在保存时生成；通过导航加入的新记录因此按新增处理。
        // The record ID stays empty for persistence to generate on save, so a record added through the navigation is treated as new.
        var binding = new ProjectConversationBinding
        {
            ProjectConversationId = _conversation.Id,
            AgentId = agentId,
            ExternalAgentName = externalAgentName,
            ProviderSessionId = providerSessionId,
            IsActive = true,
            CreateBy = _conversation.CreateBy ?? user,
            CreateTime = now,
        };
        _conversation.Bindings.Add(binding);
        return binding;
    }

    /// <summary>
    /// <para>停用指定记录；记录已经归档时保持不变并返回 false。</para>
    /// <para>Archives the specified record; an already archived record stays unchanged and false is returned.</para>
    /// </summary>
    public bool ArchiveProviderSession(Guid bindingId, string user, DateTimeOffset now)
    {
        var binding =
            _conversation.Bindings.SingleOrDefault(binding => binding.Id == bindingId)
            ?? throw new AgwException(ErrorCodes.ResourceNotFound, "Provider session binding not found.");
        if (!binding.IsActive)
        {
            return false;
        }

        Archive(binding, user, now);
        return true;
    }

    private List<ProjectConversationBinding> GetBindingGroup(Guid agentId, string externalAgentName) =>
        _conversation
            .Bindings.Where(binding =>
                binding.AgentId == agentId
                && string.Equals(binding.ExternalAgentName, externalAgentName, StringComparison.Ordinal)
            )
            .ToList();

    private static void EnsureProviderSessionUnused(
        IEnumerable<ProjectConversationBinding> group,
        string providerSessionId
    )
    {
        if (group.Any(binding => string.Equals(binding.ProviderSessionId, providerSessionId, StringComparison.Ordinal)))
        {
            throw new AgwException(
                ErrorCodes.ConversationSessionConflict,
                "The provider session has been archived and cannot be activated again."
            );
        }
    }

    private static void Archive(ProjectConversationBinding binding, string user, DateTimeOffset now)
    {
        binding.IsActive = false;
        binding.UpdateBy = user;
        binding.UpdateTime = now;
    }

    private static string ResolveTitle(string? requestedTitle, string? input) =>
        string.IsNullOrWhiteSpace(requestedTitle) ? DeriveTitle(input) : requestedTitle.Trim();
}
