using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Jellyfin.Database.Implementations.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Jellyfin.Database.Providers.Postgres.Services;

/// <summary>
/// Splits Jellyfin item navigation queries even when its repository explicitly requests a single query.
/// </summary>
internal sealed class PostgresItemQuerySplittingInterceptor : IQueryExpressionInterceptor
{
    private static readonly ItemQueryVisitor Visitor = new();

    /// <inheritdoc />
    public Expression QueryCompilationStarting(Expression queryExpression, QueryExpressionEventData eventData)
        => Visitor.Visit(queryExpression) ?? queryExpression;

    private sealed class ItemQueryVisitor : ExpressionVisitor
    {
        private static readonly MethodInfo SplitQueryMethod = typeof(RelationalQueryableExtensions)
            .GetMethods()
            .Single(method => method.Name == nameof(RelationalQueryableExtensions.AsSplitQuery)
                && method.IsGenericMethodDefinition
                && method.GetParameters().Length == 1)
            .MakeGenericMethod(typeof(BaseItemEntity));

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (node.Method.DeclaringType == typeof(RelationalQueryableExtensions)
                && node.Method.Name == nameof(RelationalQueryableExtensions.AsSingleQuery)
                && node.Method.IsGenericMethod
                && node.Method.GetGenericArguments()[0] == typeof(BaseItemEntity))
            {
                // Only replace the execution hint on media items. Keep predicates, projections,
                // ordering, paging and includes intact, and leave user/security queries unchanged.
                return Expression.Call(SplitQueryMethod, Visit(node.Arguments[0]));
            }

            return base.VisitMethodCall(node);
        }
    }
}
