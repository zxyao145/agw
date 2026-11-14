using Agw.Shared.Contracts.Conversations;
using Agw.Shared.Contracts.Events;

namespace Agw.Shared.Tests;

public sealed class ConversationKindsTests
{
    [Theory]
    [InlineData(EventSourceKind.Timer, "event:timer")]
    [InlineData(EventSourceKind.Webhook, "event:webhook")]
    [InlineData(EventSourceKind.HttpPoll, "event:httppoll")]
    [InlineData(EventSourceKind.Provider, "event:provider")]
    [InlineData(EventSourceKind.Publisher, "event:publisher")]
    public void ForEventSource_EachKind_ReturnsLowercaseEventKind(EventSourceKind kind, string expected)
    {
        Assert.Equal(expected, ConversationKinds.ForEventSource(kind));
        Assert.True(ConversationKinds.IsEvent(expected));
    }

    [Theory]
    [InlineData("native")]
    [InlineData("event:")]
    [InlineData("event:Timer")]
    [InlineData("event:unknown")]
    [InlineData("timer")]
    public void IsEvent_OtherValues_ReturnsFalse(string kind) => Assert.False(ConversationKinds.IsEvent(kind));
}
