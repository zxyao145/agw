using Agw.Jobs.Domain.Entities;

namespace Agw.Jobs.Domain.Events;

public sealed record JobCreatedDomainEvent(Job Job) : IJobDomainEvent;
