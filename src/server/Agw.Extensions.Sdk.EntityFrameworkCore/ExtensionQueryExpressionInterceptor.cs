using System.Linq.Expressions;
using Agw.Extensions.Sdk.Database;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Query.Internal;

namespace Agw.Extensions.Sdk.EntityFrameworkCore;

internal sealed class ExtensionQueryExpressionInterceptor : IQueryExpressionInterceptor
{
    public Expression QueryCompilationStarting(Expression queryExpression, QueryExpressionEventData eventData)
    {
        if (eventData.Context is not ExtensionDbContext context)
            throw new AgwException(ErrorCodes.ExtensionPackageInvalid);
        if (!context.IsCheckingOwnership && !context.IsScanningOutbox && !context.IsReadingOwnedRecords)
            new SupportedQueryVisitor().Visit(queryExpression);
        return queryExpression;
    }

    private sealed class SupportedQueryVisitor : ExpressionVisitor
    {
        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (
                node.Method.DeclaringType == typeof(EntityFrameworkQueryableExtensions)
                && node.Method.Name
                    is nameof(EntityFrameworkQueryableExtensions.IgnoreQueryFilters)
                        or nameof(EntityFrameworkQueryableExtensions.ExecuteUpdate)
                        or nameof(EntityFrameworkQueryableExtensions.ExecuteDelete)
            )
                throw UnsupportedQuery();
            return base.VisitMethodCall(node);
        }

        protected override Expression VisitExtension(Expression node)
        {
#pragma warning disable EF1001 // EF 查询根用于验证 SDK 入口。EF query roots validate SDK entrypoints.
            if (node is FromSqlQueryRootExpression or SqlQueryRootExpression)
                throw UnsupportedQuery();
#pragma warning restore EF1001
            return base.VisitExtension(node);
        }

        private static AgwException UnsupportedQuery() =>
            new(
                ErrorCodes.ExtensionPackageInvalid,
                "Extension database queries must use ownership-filtered LINQ and SaveChanges."
            );
    }
}
