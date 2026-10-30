using Jellyfin.Database.Providers.Postgres.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Xunit;

namespace Jellyfin.Database.Providers.Postgres.Tests;

/// <summary>
/// Provider options that are applied regardless of the connection string.
/// </summary>
public sealed class PostgresProviderOptionsTests
{
    /// <summary>
    /// Collection links, images and providers must not multiply the complete item payload in a joined
    /// result. Splitting also makes the execution policy explicit instead of hiding EF Core's warning.
    /// </summary>
    [Fact]
    public void CollectionNavigationsDoNotMultiplyTheCompleteItemPayload()
    {
        var options = BuildOptions(commandTimeout: 0);

        Assert.Equal(QuerySplittingBehavior.SplitQuery, ExtractRelationalOptions(options).QuerySplittingBehavior);
    }

    [Fact]
    public void CommandTimeoutIsOnlyAppliedWhenPositive()
    {
        Assert.Null(ExtractRelationalOptions(BuildOptions(commandTimeout: 0)).CommandTimeout);
        Assert.Equal(300, ExtractRelationalOptions(BuildOptions(commandTimeout: 300)).CommandTimeout);
    }

    [Fact]
    public void MigrationsAssemblyIsAlwaysConfigured()
    {
        var migrationsAssembly = ExtractRelationalOptions(BuildOptions(commandTimeout: 0)).MigrationsAssembly;

        Assert.False(string.IsNullOrWhiteSpace(migrationsAssembly));
    }

    private static DbContextOptions BuildOptions(int commandTimeout)
    {
        var builder = new DbContextOptionsBuilder();
        builder.UseNpgsql(
            "Host=localhost;Port=5432;Database=jellyfin;Username=jellyfin",
            npgsql => PostgresDatabaseProvider.ConfigureNpgsql(npgsql, commandTimeout));
        return builder.Options;
    }

    private static RelationalOptionsExtension ExtractRelationalOptions(DbContextOptions options)
        => options.Extensions.OfType<RelationalOptionsExtension>().Single();
}
