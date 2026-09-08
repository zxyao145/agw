namespace Agw.Projects.Infrastructure;

public enum ConversationHistoryWriteMode
{
    Immediate,
    Interval,
    TurnEnd,
}

public sealed class ConversationHistoryOptions
{
    public const string SectionName = "ConversationHistory";
    public ConversationHistoryWriteMode Mode { get; set; } = ConversationHistoryWriteMode.Interval;
    public int FlushIntervalSeconds { get; set; } = 5;
    public long MaxBufferedBytes { get; set; } = 16 * 1024 * 1024;
}
