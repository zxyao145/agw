namespace DSystem.Domain.Entities;

public abstract class BaseEntity
{
    public DateTime CreateTime { get; set; }
    public string? CreateBy { get; set; }
    public DateTime? UpdateTime { get; set; }
    public string? UpdateBy { get; set; }
}
