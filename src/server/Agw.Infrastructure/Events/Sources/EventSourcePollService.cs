using System.Text.Json;
using Agw.Events.Application.Persistence;
using Agw.Events.Configuration;
using Agw.Shared.Contracts.Events;
using Agw.Shared.Data.Entities.Events;
using Agw.Shared.Exceptions;
using Microsoft.Extensions.Options;

namespace Agw.Infrastructure.Events.Sources;

public sealed class EventSourcePollService : IEventSourcePollService
{
    private readonly EventHttpPoller _httpPoller;
    private readonly ExtensionEventSourcePoller _extensionPoller;
    private readonly EventHttpPollOptions _options;

    public EventSourcePollService(
        EventHttpPoller httpPoller,
        ExtensionEventSourcePoller extensionPoller,
        IOptions<EventsOptions> options
    )
    {
        _httpPoller = httpPoller;
        _extensionPoller = extensionPoller;
        _options = options.Value.HttpPoll;
    }

    public async Task<EventSourcePollTestResult> TestAsync(EventSource source, CancellationToken cancellationToken)
    {
        if (source.Kind == EventSourceKind.HttpPoll)
        {
            using var configDocument = JsonDocument.Parse(source.ConfigJson);
            var config = EventSourceConfigParser.ParseHttpPoll(configDocument.RootElement, _options);
            var items = await _httpPoller.FetchAsync(source, config, cancellationToken);
            return new EventSourcePollTestResult(items.Count);
        }

        if (source.Kind == EventSourceKind.Provider)
        {
            var result = await _extensionPoller.PollAsync(source, baseline: false, cancellationToken);
            return new EventSourcePollTestResult(result.Events.Count);
        }

        throw new AgwException(ErrorCodes.InvalidParam);
    }
}
