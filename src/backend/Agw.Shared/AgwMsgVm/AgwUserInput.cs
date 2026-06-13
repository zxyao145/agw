namespace Agw.Shared.AgwMsgVm;

public class AgwUserInput
{
    public string MessageId { get; init; } = Guid.NewGuid().ToString();
    public string? Author { get; init; } = Constants.DefaultAuthor;
    public required List<AgwContent> Contents { get; init; }
}
