using System.Linq.Expressions;

using Agw.Integrations;
using Agw.Integrations.Domain.Entities;
using Agw.Shared.Data.Repositories;

namespace Agw.Infrastructure.Repositories;

public class AppDefinitionRepo : IRepository<AppDefinition>
{
    public IQueryable<AppDefinition> Queryable => IntegrationConstants.AppList
        .Select(Clone)
        .AsQueryable();

    public Task AddAsync(AppDefinition entity)
    {
        throw CreateReadOnlyException();
    }

    public Task<AppDefinition?> GetByIdAsync(object id)
    {
        if (id is not string name || string.IsNullOrWhiteSpace(name))
        {
            return Task.FromResult<AppDefinition?>(null);
        }

        var definition = IntegrationConstants.AppList.FirstOrDefault(app =>
            string.Equals(app.Name, name, StringComparison.OrdinalIgnoreCase));

        return Task.FromResult(definition is null ? null : Clone(definition));
    }

    public Task<IReadOnlyList<AppDefinition>> ListAsync(Expression<Func<AppDefinition, bool>>? predicate = null, Func<IQueryable<AppDefinition>, IOrderedQueryable<AppDefinition>>? orderBy = null)
    {
        var query = BuildQuery(predicate, orderBy);
        IReadOnlyList<AppDefinition> result = query.ToList();
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<AppDefinition>> ListAsync(Expression<Func<AppDefinition, bool>>? predicate = null, Func<IQueryable<AppDefinition>, IOrderedQueryable<AppDefinition>>? orderBy = null, params Expression<Func<AppDefinition, object>>[] includes)
    {
        return ListAsync(predicate, orderBy);
    }

    public void Remove(AppDefinition entity)
    {
        throw CreateReadOnlyException();
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task<AppDefinition?> SingleOrDefaultAsync(Expression<Func<AppDefinition, bool>> predicate, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Queryable.SingleOrDefault(predicate));
    }

    public void Update(AppDefinition entity)
    {
        throw CreateReadOnlyException();
    }

    private IQueryable<AppDefinition> BuildQuery(
        Expression<Func<AppDefinition, bool>>? predicate,
        Func<IQueryable<AppDefinition>, IOrderedQueryable<AppDefinition>>? orderBy)
    {
        var query = Queryable;

        if (predicate != null)
        {
            query = query.Where(predicate);
        }

        if (orderBy != null)
        {
            query = orderBy(query);
        }

        return query;
    }

    private static AppDefinition Clone(AppDefinition definition)
    {
        return new AppDefinition
        {
            Name = definition.Name,
            DisplayName = definition.DisplayName,
            Category = definition.Category,
            Provider = definition.Provider,
            Description = definition.Description,
            AuthUrl = definition.AuthUrl,
            TokenEndpoint = definition.TokenEndpoint,
            SubjectField = definition.SubjectField,
            Scopes = [.. definition.Scopes],
            UsePkce = definition.UsePkce,
            Tags = [.. definition.Tags],
            ToolNames = [.. definition.ToolNames]
        };
    }

    private static NotSupportedException CreateReadOnlyException()
    {
        return new NotSupportedException("App definitions are loaded from IntegrationConstants.AppList and cannot be persisted.");
    }
}
