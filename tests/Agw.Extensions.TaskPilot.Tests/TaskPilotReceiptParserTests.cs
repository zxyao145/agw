using Xunit;

namespace Agw.Extensions.TaskPilot.Tests;

public sealed class TaskPilotReceiptParserTests
{
    [Fact]
    public void Parse_ReceiptBlock_ReadsLastTaskpilotBlock()
    {
        string message = """
            Completed the first step.

            ```taskpilot
            {"notice":[{"title":"Old","text":"Old body"}]}
            ```

            ```taskpilot
            {"notice":[{"title":"Current","text":"Current body","fyi":true}]}
            ```
            """;

        TaskPilotReceiptParseResult result = TaskPilotReceiptParser.Parse(
            message,
            TaskPilotJsonContext.Default.Options
        );

        Assert.True(result.IsValid);
        Assert.Equal("Current", Assert.Single(result.Receipt!.Notice).Title);
        Assert.Equal(TaskPilotRoundOutcome.Completed, result.Outcome);
    }

    [Fact]
    public void Validate_Receipt_RejectsUnknownFields()
    {
        string message = """
            Completed.

            ```taskpilot
            {"unknown":true}
            ```
            """;

        TaskPilotReceiptParseResult result = TaskPilotReceiptParser.Parse(
            message,
            TaskPilotJsonContext.Default.Options
        );

        Assert.False(result.IsValid);
        Assert.Null(result.Receipt);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Fact]
    public void Validate_Receipt_RejectsEventTypeThatIsNotAString()
    {
        string message = """
            Completed.

            ```taskpilot
            {"events":[{"type":42}]}
            ```
            """;

        TaskPilotReceiptParseResult result = TaskPilotReceiptParser.Parse(
            message,
            TaskPilotJsonContext.Default.Options
        );

        Assert.False(result.IsValid);
        Assert.Null(result.Receipt);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }
}
