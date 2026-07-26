namespace Agw.Shared.Data.Abstractions;

public interface IEntityCreateTime
{
    DateTimeOffset CreateTime { get; set; }
}

public interface IEntityCreator : IEntityCreateTime
{
    string? CreateBy { get; set; }
}
