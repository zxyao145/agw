using Agw.Shared.Contracts.Events;

namespace Agw.Events.Application.Persistence;

public interface IEventTemplateValidator
{
    void Validate(CloudEventDraft draft);
}
