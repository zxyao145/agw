using Agw.Shared.Data.Entities.Jobs;
using Agw.Shared.EventBus.Abstractions;

namespace Agw.Shared.Domain.Events;

public sealed record JobUpdatedDomainEvent(Job Job) : IDomainEvent;
