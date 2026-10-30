using Jellyfin.Database.Providers.Postgres.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Jellyfin.Database.Providers.Postgres.Tests;

/// <summary>Only media-item single-query hints should be rewritten.</summary>
public sealed class ItemQuerySplittingTests
{
    [Fact]
    public void ExplicitSplitItemQueryIsUnchanged()
    {
        using var context = TestEnvironment.CreateModelContext();
        var expression = context.BaseItems.AsSplitQuery().Expression;
        Assert.Same(expression, new PostgresItemQuerySplittingInterceptor().QueryCompilationStarting(expression, null!));
    }

    [Fact]
    public void ExplicitSingleUserQueryIsUnchanged()
    {
        using var context = TestEnvironment.CreateModelContext();
        var expression = context.Users.AsSingleQuery().Expression;
        Assert.Same(expression, new PostgresItemQuerySplittingInterceptor().QueryCompilationStarting(expression, null!));
    }
}
