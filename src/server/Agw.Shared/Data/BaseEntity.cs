namespace Agw.Shared.Data;

public abstract class BaseEntity
{
    public DateTimeOffset CreateTime { get; set; }
    public string? CreateBy { get; set; }
    public DateTimeOffset? UpdateTime { get; set; }
    public string? UpdateBy { get; set; }
}
