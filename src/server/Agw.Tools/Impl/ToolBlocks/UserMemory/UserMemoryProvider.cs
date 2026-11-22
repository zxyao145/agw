using System.Text;
using Agw.Tools.Application;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Tools.Impl.ToolBlocks.UserMemory;

public sealed class UserMemoryProvider : AIContextProvider, IConversationContextSource
{
    public const string ListToolName = "user_memory_list";
    public const string ReadToolName = "user_memory_read";
    public const string WriteToolName = "user_memory_write";
    public const string DeleteToolName = "user_memory_delete";

    private const int MaxContextEntries = 50;
    private const string Instructions = $$"""
        ## User Memory
        {{
            "The `user_memory_*` tools store private memory that follows the current user across projects and "
            + "conversations: preferences, personal conventions, recurring context."
        }}
        Knowledge that belongs to one project goes to project memory.
        Up to 50 memory bodies are provided as context.
        Use `user_memory_list` and `user_memory_read` for memories beyond those, and keep memories current.
        """;

    private readonly IServiceScopeFactory _serviceScopeFactory;
    private AITool[]? _tools;

    public UserMemoryProvider(IServiceScopeFactory serviceScopeFactory)
    {
        ArgumentNullException.ThrowIfNull(serviceScopeFactory);
        _serviceScopeFactory = serviceScopeFactory;
    }

    public override IReadOnlyList<string> StateKeys => [];

    protected override ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default
    )
    {
        return ValueTask.FromResult(new AIContext { Instructions = Instructions, Tools = _tools ??= CreateTools() });
    }

    public async ValueTask<ChatMessage?> CreateContextMessageAsync(CancellationToken cancellationToken = default)
    {
        var content = await ReadContextContentAsync(cancellationToken).ConfigureAwait(false);
        if (content is null)
            return null;

        var message = new ChatMessage(ChatRole.User, content).WithAgentRequestMessageSource(
            AgentRequestMessageSourceType.AIContextProvider,
            ConversationHistoryMetadata.UserMemorySourceId
        );
        ConversationHistoryMetadata.ExcludeFromPersistence(message);
        return message;
    }

    string IConversationContextSource.Title => "User Memory";

    ValueTask<string?> IConversationContextSource.GetContentAsync(CancellationToken cancellationToken) =>
        ReadContextContentAsync(cancellationToken);

    private async ValueTask<string?> ReadContextContentAsync(CancellationToken cancellationToken)
    {
        var memories = await ListContextAsync(MaxContextEntries, cancellationToken).ConfigureAwait(false);
        if (memories.Count == 0)
        {
            return null;
        }

        var content = new StringBuilder("# User Memories\n\n");
        foreach (var memory in memories)
        {
            content.Append("## ").Append(SingleLine(memory.Name)).AppendLine().AppendLine().Append(memory.Content);
            if (!memory.Content.EndsWith('\n'))
            {
                content.AppendLine();
            }
            content.AppendLine();
        }

        return "The following Markdown is the current user's private memory content. "
            + "Apply it as user-provided context.\n\n"
            + content;
    }

    [Description(
        "List the current user's memories with names, descriptions, and update times. Does not return memory content."
    )]
    private async Task<IReadOnlyList<UserMemoryToolListItem>> ListAsync(CancellationToken cancellationToken = default)
    {
        var memories = await ListIndexAsync(limit: null, cancellationToken).ConfigureAwait(false);
        return memories
            .Select(memory => new UserMemoryToolListItem(
                memory.Name,
                memory.Description,
                memory.UpdateTime ?? memory.CreateTime
            ))
            .ToList();
    }

    [Description("Read the full Markdown content of one user memory by its case-insensitive name.")]
    private async Task<string> ReadAsync(string name, CancellationToken cancellationToken = default)
    {
        await using var scope = _serviceScopeFactory.CreateAsyncScope();
        var appService = scope.ServiceProvider.GetRequiredService<UserMemoryAppService>();
        var memory = await appService.GetByNameAsync(name, cancellationToken).ConfigureAwait(false);
        return memory?.Content ?? $"User memory '{name}' not found.";
    }

    [Description(
        "Create or overwrite a user memory by name. Omit description to preserve an existing description; pass an empty description to clear it."
    )]
    private async Task<string> WriteAsync(
        string name,
        string content,
        string? description = null,
        CancellationToken cancellationToken = default
    )
    {
        await using var scope = _serviceScopeFactory.CreateAsyncScope();
        var appService = scope.ServiceProvider.GetRequiredService<UserMemoryAppService>();
        var memory = await appService
            .UpsertByNameAsync(name, content, description, cancellationToken)
            .ConfigureAwait(false);
        return $"User memory '{memory.Name}' written.";
    }

    [Description("Delete one user memory by its case-insensitive name.")]
    private async Task<string> DeleteAsync(string name, CancellationToken cancellationToken = default)
    {
        await using var scope = _serviceScopeFactory.CreateAsyncScope();
        var appService = scope.ServiceProvider.GetRequiredService<UserMemoryAppService>();
        var deleted = await appService.DeleteByNameAsync(name, cancellationToken).ConfigureAwait(false);
        return deleted ? $"User memory '{name}' deleted." : $"User memory '{name}' not found.";
    }

    private AITool[] CreateTools() =>
        [
            AIFunctionFactory.Create(
                (Func<CancellationToken, Task<IReadOnlyList<UserMemoryToolListItem>>>)ListAsync,
                new AIFunctionFactoryOptions { Name = ListToolName }
            ),
            AIFunctionFactory.Create(
                (Func<string, CancellationToken, Task<string>>)ReadAsync,
                new AIFunctionFactoryOptions { Name = ReadToolName }
            ),
            AIFunctionFactory.Create(
                (Func<string, string, string?, CancellationToken, Task<string>>)WriteAsync,
                new AIFunctionFactoryOptions { Name = WriteToolName }
            ),
            AIFunctionFactory.Create(
                (Func<string, CancellationToken, Task<string>>)DeleteAsync,
                new AIFunctionFactoryOptions { Name = DeleteToolName }
            ),
        ];

    private async Task<IReadOnlyList<UserMemorySummary>> ListIndexAsync(int? limit, CancellationToken cancellationToken)
    {
        await using var scope = _serviceScopeFactory.CreateAsyncScope();
        var appService = scope.ServiceProvider.GetRequiredService<UserMemoryAppService>();
        return await appService.ListIndexAsync(limit, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<UserMemoryContextEntry>> ListContextAsync(
        int limit,
        CancellationToken cancellationToken
    )
    {
        await using var scope = _serviceScopeFactory.CreateAsyncScope();
        var appService = scope.ServiceProvider.GetRequiredService<UserMemoryAppService>();
        return await appService.ListContextAsync(limit, cancellationToken).ConfigureAwait(false);
    }

    private static string SingleLine(string value) => value.Replace('\r', ' ').Replace('\n', ' ');
}

public sealed record UserMemoryToolListItem(string Name, string? Description, DateTimeOffset UpdatedAt);
