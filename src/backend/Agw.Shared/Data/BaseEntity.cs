using System.ComponentModel.DataAnnotations.Schema;

using Agw.Shared.EventBus.Abstractions;

namespace Agw.Shared.Data;

public abstract class BaseEntity
{
    private readonly List<IDomainEvent> _domainEvents = [];

    public DateTime CreateTime { get; set; }
    public string? CreateBy { get; set; }
    public DateTime? UpdateTime { get; set; }
    public string? UpdateBy { get; set; }

    [NotMapped]
    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    public void AddDomainEvent(IDomainEvent domainEvent)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        _domainEvents.Add(domainEvent);
    }

    public void ClearDomainEvents()
    {
        _domainEvents.Clear();
    }
}
