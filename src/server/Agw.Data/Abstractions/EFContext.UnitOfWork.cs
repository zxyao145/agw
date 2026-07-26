namespace Agw.Shared.Data.Abstractions;

public partial class EFContext
{
    #region IUnitOfWork
    public virtual async Task<bool> SaveEntitiesAsync(CancellationToken cancellationToken = default)
    {
        await base.SaveChangesAsync(cancellationToken);
        return true;
    }
    #endregion
}
