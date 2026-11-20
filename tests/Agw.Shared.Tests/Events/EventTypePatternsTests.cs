using Agw.Shared.Contracts.Events;

namespace Agw.Shared.Tests.Events;

public sealed class EventTypePatternsTests
{
    [Theory]
    [InlineData("demo.*", "demo.ticket.created", true)]
    [InlineData("demo.*", "demox.created", false)]
    [InlineData("*", "any.type", true)]
    [InlineData("demo.ticket.created", "demo.ticket.created", true)]
    [InlineData("demo.ticket.created", "demo.ticket.updated", false)]
    public void Matches_PatternAndType_ReturnsExpectedResult(string pattern, string eventType, bool expected) =>
        Assert.Equal(expected, EventTypePatterns.Matches(pattern, eventType));

    [Theory]
    [InlineData("demo.*", true)]
    [InlineData("demo.ticket.created", true)]
    [InlineData("demo*", false)]
    [InlineData("*", false)]
    [InlineData("a.*.b", false)]
    [InlineData(" demo", false)]
    [InlineData("", false)]
    public void IsValidPattern_Pattern_ReturnsExpectedResult(string pattern, bool expected) =>
        Assert.Equal(expected, EventTypePatterns.IsValidPattern(pattern));

    [Fact]
    public void IsExactType_WildcardAndExact_ReturnsExpectedResult()
    {
        Assert.False(EventTypePatterns.IsExactType("demo.*"));
        Assert.True(EventTypePatterns.IsExactType("demo.created"));
    }

    [Fact]
    public void IsReserved_AgwType_ReturnsTrue() =>
        Assert.True(EventTypePatterns.IsReserved("agw.execution.completed"));

    [Fact]
    public void MatchesAny_OneMatchingPattern_ReturnsTrue() =>
        Assert.True(EventTypePatterns.MatchesAny(["other.*", "demo.*"], "demo.created"));
}
