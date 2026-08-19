using System.Linq.Expressions;
using Agw.Shared.Data.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Agw.Infrastructure.Data;

public static class SoftDeleteQueryFilterNames
{
    public const string SoftDelete = "SoftDeleteFilter";
}

internal static class SoftDeleteModelBuilderExtensions
{
    public static void ApplySoftDeleteQueryFilters(this ModelBuilder modelBuilder)
    {
        foreach (
            var entityType in modelBuilder
                .Model.GetEntityTypes()
                .Where(entityType => typeof(ISoftDelete).IsAssignableFrom(entityType.ClrType))
        )
        {
            var parameter = Expression.Parameter(entityType.ClrType, "entity");
            var isDeleted = Expression.Property(parameter, nameof(ISoftDelete.IsDeleted));
            var filter = Expression.Lambda(Expression.Equal(isDeleted, Expression.Constant(false)), parameter);

            modelBuilder.Entity(entityType.ClrType).HasQueryFilter(SoftDeleteQueryFilterNames.SoftDelete, filter);
        }
    }
}
