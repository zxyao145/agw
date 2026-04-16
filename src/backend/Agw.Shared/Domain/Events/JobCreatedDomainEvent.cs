using Agw.Shared.Data.Entities.Jobs;
using Agw.Shared.EventBus.Abstractions;

namespace Agw.Shared.Domain.Events;

public sealed record JobCreatedDomainEvent(Job Job) : IDomainEvent;
