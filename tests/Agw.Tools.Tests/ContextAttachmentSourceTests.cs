using Agw.Tools.Impl.ToolBlocks.Mode;
using Agw.Tools.Impl.ToolBlocks.Todo;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Tools.Tests;

public sealed class ContextAttachmentSourceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task EvaluateAsync_PlanModeActive_ReturnsFullThenSparseReminders()
    {
        var source = CreatePlanModeSource("plan");
        var session = new TestAgentSession();
        var first = await source.EvaluateAsync(session, [], Ct);
        Assert.Equal(ContextAttachmentTypes.PlanMode, first!.Type);
        Assert.StartsWith("Plan mode is active.", first.Text);
        Assert.Contains("PlanModeToolNotAllowed", first.Text, StringComparison.Ordinal);
        Assert.Contains("Plan mode is enforced by the server.\nTools", first.Text.ReplaceLineEndings("\n"));

        var plan = Attachment(ContextAttachmentTypes.PlanMode);
        Assert.Null(await source.EvaluateAsync(session, [plan, .. AssistantMessages(4)], Ct));
        var sparse = await source.EvaluateAsync(session, [plan, .. AssistantMessages(5)], Ct);
        Assert.Equal(
            new ContextAttachment(
                ContextAttachmentTypes.PlanMode,
                "Plan mode is still active, so follow the Plan mode instructions given earlier in this conversation."
            ),
            sparse
        );

        var fiveRounds = Enumerable.Range(0, 5).SelectMany(_ => new[] { plan }.Concat(AssistantMessages(5))).ToList();
        Assert.Equal(first, await source.EvaluateAsync(session, fiveRounds, Ct));
        Assert.Equal(
            first,
            await source.EvaluateAsync(session, [plan, Attachment(ContextAttachmentTypes.PlanModeExit)], Ct)
        );
    }

    [Theory]
    [InlineData(0, "Plan mode is active.")]
    [InlineData(5, "Plan mode is active.")]
    [InlineData(10, "Plan mode is active.")]
    [InlineData(15, "Plan mode is still active,")]
    [InlineData(20, "Plan mode is still active,")]
    public async Task EvaluateAsync_PlanReminderIsDue_UsesFullTextOnlyForFirstSixthAndEleventh(
        int priorReminders,
        string expectedHeading
    )
    {
        var source = CreatePlanModeSource("plan");
        var messages = Enumerable
            .Range(0, priorReminders)
            .SelectMany(_ => new[] { Attachment(ContextAttachmentTypes.PlanMode) }.Concat(AssistantMessages(5)))
            .ToArray();

        var attachment = await source.EvaluateAsync(new TestAgentSession(), messages, Ct);

        Assert.NotNull(attachment);
        Assert.StartsWith(expectedHeading, attachment.Text);
    }

    [Fact]
    public async Task EvaluateAsync_ModeBackToExecute_ReturnsPlanModeExitOnce()
    {
        var source = CreatePlanModeSource("execute");
        var session = new TestAgentSession();
        var plan = Attachment(ContextAttachmentTypes.PlanMode);
        var exit = Attachment(ContextAttachmentTypes.PlanModeExit);
        Assert.Null(await source.EvaluateAsync(session, [], Ct));
        Assert.Equal(
            new ContextAttachment(
                ContextAttachmentTypes.PlanModeExit,
                "Plan mode has ended.\nThe conversation is in Execute mode."
            ),
            await source.EvaluateAsync(session, [plan, .. AssistantMessages(1)], Ct)
        );
        Assert.Null(await source.EvaluateAsync(session, [plan, exit, .. AssistantMessages(10)], Ct));
    }

    [Theory]
    [InlineData(9, null)]
    [InlineData(12, 9)]
    public async Task EvaluateAsync_TodoCallOrReminderIsRecent_ReturnsNull(int sinceCall, int? sinceReminder)
    {
        using var provider = new AgwTodoProvider();
        var source = new TodoReminderAttachmentSource(provider, new TodoToolBlock().Descriptor.MemberToolNames);

        var attachment = await source.EvaluateAsync(new TestAgentSession(), TodoHistory(sinceCall, sinceReminder), Ct);

        Assert.Null(attachment);
    }

    [Theory]
    [InlineData(10, null)]
    [InlineData(12, 10)]
    [InlineData(null, null)]
    public async Task EvaluateAsync_TodoToolsUnusedForTenAssistantMessages_ReturnsTodoReminder(
        int? sinceCall,
        int? sinceReminder
    )
    {
        using var provider = new AgwTodoProvider();
        var source = new TodoReminderAttachmentSource(provider, new TodoToolBlock().Descriptor.MemberToolNames);

        var attachment = await source.EvaluateAsync(new TestAgentSession(), TodoHistory(sinceCall, sinceReminder), Ct);

        Assert.NotNull(attachment);
        Assert.Equal(ContextAttachmentTypes.TodoReminder, attachment.Type);
        Assert.StartsWith("The todo tools have not been used recently.\nUse them", attachment.Text);
        Assert.Contains("### Current todo list", attachment.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EvaluateAsync_PrefixedCallIsOutsideTodoBlock_ReturnsTodoReminder()
    {
        using var provider = new AgwTodoProvider();
        var source = new TodoReminderAttachmentSource(provider, new TodoToolBlock().Descriptor.MemberToolNames);
        ChatMessage[] messages =
        [
            .. AssistantMessages(9),
            new(ChatRole.Assistant, [new FunctionCallContent("other", "todos_external")]),
        ];

        var attachment = await source.EvaluateAsync(new TestAgentSession(), messages, Ct);

        Assert.Equal(ContextAttachmentTypes.TodoReminder, attachment?.Type);
    }

    private static List<ChatMessage> TodoHistory(int? sinceCall, int? sinceReminder)
    {
        List<ChatMessage> messages = [];
        if (sinceCall is not null)
            messages.Add(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call", "todos_add")]));
        messages.AddRange(AssistantMessages((sinceCall ?? 10) - (sinceReminder ?? 0)));
        if (sinceReminder is { } count)
            messages.AddRange([Attachment(ContextAttachmentTypes.TodoReminder), .. AssistantMessages(count)]);

        return messages;
    }

    private static PlanModeAttachmentSource CreatePlanModeSource(string mode) =>
        new(new AgentModeProvider(new AgentModeProviderOptions { DefaultMode = mode }));

    private static ChatMessage Attachment(string type)
    {
        var message = new ChatMessage(ChatRole.User, "reminder");
        ContextAttachmentHistory.SetAttachmentType(message, type);
        return message;
    }

    private static IEnumerable<ChatMessage> AssistantMessages(int count) =>
        Enumerable.Range(0, count).Select(index => new ChatMessage(ChatRole.Assistant, $"answer {index}"));

    private sealed class TestAgentSession : AgentSession;
}
