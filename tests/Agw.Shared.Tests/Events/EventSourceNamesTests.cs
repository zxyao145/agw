using Agw.Shared.Contracts.Events;
using Agw.Shared.Exceptions;

namespace Agw.Shared.Tests.Events;

public sealed class EventSourceNamesTests
{
    [Theory]
    [InlineData("webhook.demo-ticket", EventSourceKind.Webhook, "user")]
    [InlineData("http.orders.v2", EventSourceKind.HttpPoll, "user")]
    [InlineData("timer.daily", EventSourceKind.Timer, "user")]
    [InlineData("agw.timer.job-1-20261114", EventSourceKind.Timer, "job")]
    [InlineData("ext.taskpilot", EventSourceKind.Provider, "user")]
    [InlineData("ext.taskpilot-2", EventSourceKind.Publisher, "ext:019a4c17-5f00-7a80-8b3d-2b732f48a101")]
    [InlineData("agw.execution", EventSourceKind.Publisher, "agw")]
    public void Validate_ValidName_DoesNotThrow(string name, EventSourceKind kind, string managedBy) =>
        EventSourceNames.Validate(name, kind, managedBy);

    [Theory]
    [InlineData("webhook.Demo", EventSourceKind.Webhook, "user")]
    [InlineData("webhook.demo..x", EventSourceKind.Webhook, "user")]
    [InlineData("webhook.-demo", EventSourceKind.Webhook, "user")]
    [InlineData("webhook.demo ticket", EventSourceKind.Webhook, "user")]
    [InlineData("webhook.demo-", EventSourceKind.Webhook, "user")]
    [InlineData("http.demo", EventSourceKind.Webhook, "user")]
    [InlineData("agw.timer.daily", EventSourceKind.Timer, "user")]
    [InlineData("agw.custom", EventSourceKind.Publisher, "agw")]
    [InlineData("ext.demo", EventSourceKind.Webhook, "user")]
    public void Validate_InvalidName_ThrowsInvalidParam(string name, EventSourceKind kind, string managedBy)
    {
        var error = Assert.Throws<AgwException>(() => EventSourceNames.Validate(name, kind, managedBy));

        Assert.Equal(ErrorCodes.InvalidParam.Code, error.Code);
    }

    [Fact]
    public void Validate_NameLongerThan200_ThrowsInvalidParam()
    {
        var name = "webhook." + new string('a', 193);

        var error = Assert.Throws<AgwException>(() => EventSourceNames.Validate(name, EventSourceKind.Webhook, "user"));

        Assert.Equal(ErrorCodes.InvalidParam.Code, error.Code);
    }

    [Fact]
    public void PrefixFor_InvalidManager_ThrowsInvalidParam()
    {
        var error = Assert.Throws<AgwException>(() => EventSourceNames.PrefixFor(EventSourceKind.Publisher, "user"));

        Assert.Equal(ErrorCodes.InvalidParam.Code, error.Code);
    }

    [Fact]
    public void BuiltInSources_KnownAndUnknownNames_ReturnDeclarationsOrInvalidParam()
    {
        var execution = EventBuiltInSources.Get(EventBuiltInSources.Execution);

        Assert.Equal("/agw/executions", execution.CeSource);
        Assert.Equal(["agw.execution.completed"], execution.EventTypes);
        Assert.Equal(2, EventBuiltInSources.All.Count);
        var error = Assert.Throws<AgwException>(() => EventBuiltInSources.Get("agw.unknown"));
        Assert.Equal(ErrorCodes.InvalidParam.Code, error.Code);
    }
}
