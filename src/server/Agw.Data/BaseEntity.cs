using System.ComponentModel.DataAnnotations;
using Agw.Shared.Data.Abstractions;

namespace Agw.Shared.Data;

public interface IEntityAudit : IEntityCreator, IEntityModifier { }

public abstract class BaseEntity : IEntityAudit
{
    public DateTimeOffset CreateTime { get; set; }
    public string? CreateBy { get; set; }
    public DateTimeOffset? UpdateTime { get; set; }
    public string? UpdateBy { get; set; }
}

public abstract class EntityBase<TKey> : BaseEntity
{
    [Key]
    public virtual TKey Id { get; set; } = default!;
}
