using Agw.Events.Application.Persistence;
using Agw.Shared.Contracts.Events;

namespace Agw.Infrastructure.Events;

public sealed class EventTemplateValidator : IEventTemplateValidator
{
    private readonly CloudEventEnvelopeSerializer _serializer;

    public EventTemplateValidator(CloudEventEnvelopeSerializer serializer)
    {
        _serializer = serializer;
    }

    public void Validate(CloudEventDraft draft)
    {
        var extensions = new Dictionary<string, CloudEventAttributeValue>();
        if (draft.Scope is not null)
            extensions["scope"] = new(CloudEventAttributeKind.String, draft.Scope);
        if (draft.Tags is not null)
            extensions["tags"] = new(CloudEventAttributeKind.String, EventTagCodec.Encode(draft.Tags));
        _serializer.Validate(
            new CloudEventEnvelope
            {
                Id = draft.Id,
                Source = draft.Source!,
                Type = draft.Type,
                Time = draft.Time,
                Subject = draft.Subject,
                DataContentType = draft.DataContentType,
                DataSchema = draft.DataSchema,
                Data = draft.Data,
                Extensions = extensions,
            }
        );
    }
}
