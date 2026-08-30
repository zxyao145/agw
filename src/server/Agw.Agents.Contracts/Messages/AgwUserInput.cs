using Agw.Shared;

namespace Agw.Agents.Contracts.Messages;

public class AgwUserInput
{
    public string MessageId { get; init; } = Guid.CreateVersion7().ToString();
    public string? Author { get; init; } = Constants.DefaultInputAuthor;
    public required List<AgwContent> Contents { get; init; }
}
