namespace Agw.Shared.Data.Abstractions;

public interface IEntityModifyTime
{
    DateTimeOffset? UpdateTime { get; set; }
}

public interface IEntityModifier : IEntityModifyTime
{
    string? UpdateBy { get; set; }
}
