namespace DSystem.SessionRecords.Repositories;

public interface ISessionRecordsUnitOfWork
{
    Task<int> SaveChangesAsync();
}
