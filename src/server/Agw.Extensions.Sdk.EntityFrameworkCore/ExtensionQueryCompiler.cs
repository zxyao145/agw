using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.Internal;
using Microsoft.EntityFrameworkCore.Storage;

namespace Agw.Extensions.Sdk.EntityFrameworkCore;

internal sealed class ExtensionQueryCompiler : QueryCompiler
{
    public ExtensionQueryCompiler(
        IQueryContextFactory queryContextFactory,
        ICompiledQueryCache compiledQueryCache,
        ICompiledQueryCacheKeyGenerator compiledQueryCacheKeyGenerator,
        IDatabase database,
        IDiagnosticsLogger<DbLoggerCategory.Query> logger,
        ICurrentDbContext currentContext,
        IEvaluatableExpressionFilter evaluatableExpressionFilter,
        IModel model
    )
        : base(
            queryContextFactory,
            compiledQueryCache,
            compiledQueryCacheKeyGenerator,
            database,
            logger,
            currentContext,
            evaluatableExpressionFilter,
            model
        ) { }

    public override Expression ExtractParameters(
        Expression query,
        Dictionary<string, object?> parameters,
        IDiagnosticsLogger<DbLoggerCategory.Query> logger,
        bool compiledQuery = false,
        bool generateContextAccessors = false
    ) =>
        base.ExtractParameters(
            new CapturedFieldVisitor().Visit(query),
            parameters,
            logger,
            compiledQuery,
            generateContextAccessors
        );

    private sealed class CapturedFieldVisitor : ExpressionVisitor
    {
        private readonly Dictionary<Expression, Expression> _captures = new(ExpressionEqualityComparer.Instance);

        protected override Expression VisitMember(MemberExpression node)
        {
            if (
                node is { Member: FieldInfo field, Expression: ConstantExpression { Value: not null } capture }
                && field.IsCollectible
            )
            {
                if (!_captures.TryGetValue(node, out Expression? replacement))
                {
                    // Keep captured values parameterized with framework-owned field metadata.
                    // 使用 framework 自有的字段 metadata，保留捕获变量的参数化行为。
                    MemberExpression value = Expression.Field(
                        Expression.Constant(new StrongBox<object?>(field.GetValue(capture.Value))),
                        nameof(StrongBox<object?>.Value)
                    );
                    replacement = value.Type == node.Type ? value : Expression.Convert(value, node.Type);
                    _captures.Add(node, replacement);
                }

                return replacement;
            }

            return base.VisitMember(node);
        }
    }
}
