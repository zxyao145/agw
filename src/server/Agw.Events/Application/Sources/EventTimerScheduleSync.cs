using System.Text.Json;
using System.Xml;
using Agw.Events.Application.Persistence;
using Agw.Events.Contracts;
using Agw.Shared.Contracts.Events;
using Agw.Shared.Data.Entities.Events;
using Agw.Shared.Exceptions;

namespace Agw.Events.Application.Sources;

public sealed class EventTimerScheduleSync
{
    private readonly IEventScheduler _scheduler;

    public EventTimerScheduleSync(IEventScheduler scheduler)
    {
        _scheduler = scheduler;
    }

    public Task SyncAsync(EventSource source, long requestSeq, CancellationToken cancellationToken)
    {
        var businessKey = $"source:{source.Id}";
        if (!source.Enabled || source.DeletedAt is not null)
        {
            return _scheduler.CancelAsync(businessKey, requestSeq, cancellationToken);
        }

        using var configDocument = JsonDocument.Parse(source.ConfigJson);
        var config = EventSourceConfigParser.ParseTimer(configDocument.RootElement);
        return _scheduler.UpsertAsync(
            new ScheduleRequest
            {
                BusinessKey = businessKey,
                RequestSeq = requestSeq,
                Kind = config.Kind switch
                {
                    "Once" => ScheduleKind.Once,
                    "Interval" => ScheduleKind.Interval,
                    "Cron" => ScheduleKind.Cron,
                    _ => throw new AgwException(ErrorCodes.InvalidParam),
                },
                At = config.Kind == "Once" ? config.At : null,
                Interval = config.Interval is null ? null : XmlConvert.ToTimeSpan(config.Interval),
                Cron = config.Cron,
                TimeZone = config.TimeZone,
                Event = new CloudEventDraft
                {
                    Id = "source-template",
                    Type = config.Event.Type,
                    Source = source.CeSource,
                    Time = config.Event.Time,
                    Subject = config.Event.Subject,
                    DataContentType = config.Event.DataContentType,
                    DataSchema = config.Event.DataSchema,
                    Data = ParseTemplateData(config.Event),
                    Scope = config.Event.Scope,
                    Tags = config.Event.Tags,
                    Summary = config.Event.Summary,
                    Link = config.Event.Link,
                },
            },
            cancellationToken
        );
    }

    private static CloudEventData? ParseTemplateData(EventSourceEventTemplate template)
    {
        if (template.DataBase64 is not null)
        {
            return new CloudEventData
            {
                Kind = CloudEventDataKind.Binary,
                Binary = Convert.FromBase64String(template.DataBase64),
            };
        }

        if (template.Data is { } data)
        {
            return new CloudEventData
            {
                Kind = CloudEventDataKind.Json,
                Text = data.GetRawText(),
                Json = data.Clone(),
            };
        }

        return null;
    }
}
