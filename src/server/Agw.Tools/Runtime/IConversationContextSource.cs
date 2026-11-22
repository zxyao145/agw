namespace Agw.Tools.Runtime;

public interface IConversationContextSource
{
    string Title { get; }

    ValueTask<string?> GetContentAsync(CancellationToken cancellationToken);
}
