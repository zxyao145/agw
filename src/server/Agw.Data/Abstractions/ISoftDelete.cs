namespace Agw.Shared.Data.Abstractions;

public interface ISoftDelete
{
    bool IsDeleted { get; set; }
}

public interface ISoftDeleteAudit : ISoftDelete
{
    DateTimeOffset? DeletionTime { get; set; }

    string? DeleteBy { get; set; }
}
