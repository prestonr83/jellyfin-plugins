using System;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Jellyfin.Database.Implementations.Entities;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Jellyfin.Database.Providers.Postgres.Services;

/// <summary>Preserves set operations in folder-aware rating filters so PostgreSQL computes matching IDs once.</summary>
internal sealed class PostgresRatingFilterInterceptor : IQueryExpressionInterceptor
{
    /// <inheritdoc />
    public Expression QueryCompilationStarting(Expression queryExpression, QueryExpressionEventData eventData)
        => new RatingVisitor().Visit(queryExpression) ?? queryExpression;

    private sealed class RatingVisitor : ExpressionVisitor
    {
        private static readonly MethodInfo UnionMethod = typeof(Queryable).GetMethods()
            .Single(method => method.Name == nameof(Queryable.Union)
                && method.IsGenericMethodDefinition && method.GetParameters().Length == 2)
            .MakeGenericMethod(typeof(Guid));

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (IsGuidOperation(node, nameof(Queryable.Distinct))
                && node.Arguments.Count == 1
                && node.Arguments[0] is MethodCallExpression source
                && IsGuidOperation(source, nameof(Queryable.Concat))
                && RatingFinder.ContainsRating(source))
            {
                // Distinct(A.Concat(B).Concat(C)) and A.Union(B).Union(C) have
                // identical set semantics. EF removes Distinct beneath Contains;
                // retaining UNION avoids a repeatedly executed parallel append.
                return node.Update(node.Object, [ConvertConcatChain(source)]);
            }

            return base.VisitMethodCall(node);
        }

        private static bool IsGuidOperation(MethodCallExpression node, string name)
            => node.Method.DeclaringType == typeof(Queryable)
                && node.Method.Name == name && node.Method.IsGenericMethod
                && node.Method.GetGenericArguments()[0] == typeof(Guid);

        private static Expression ConvertConcatChain(Expression expression)
            => expression is MethodCallExpression node && IsGuidOperation(node, nameof(Queryable.Concat))
                ? Expression.Call(UnionMethod, ConvertConcatChain(node.Arguments[0]), ConvertConcatChain(node.Arguments[1]))
                : expression;
    }

    private sealed class RatingFinder : ExpressionVisitor
    {
        private bool _found;

        internal static bool ContainsRating(Expression expression)
        {
            var finder = new RatingFinder();
            finder.Visit(expression);
            return finder._found;
        }

        protected override Expression VisitMember(MemberExpression node)
        {
            if (node.Member.DeclaringType == typeof(BaseItemEntity)
                && node.Member.Name == nameof(BaseItemEntity.OfficialRating))
            {
                _found = true;
            }

            return base.VisitMember(node);
        }
    }
}
