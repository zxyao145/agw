using Agw.Shared.Data.Entities.Jobs;

namespace Agw.Jobs.Domain.Events;

public sealed record JobCreatedDomainEvent(Job Job) : IJobDomainEvent;
