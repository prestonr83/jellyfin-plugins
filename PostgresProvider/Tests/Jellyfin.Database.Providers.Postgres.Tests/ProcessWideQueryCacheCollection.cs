using Xunit;

namespace Jellyfin.Database.Providers.Postgres.Tests;

/// <summary>
/// Isolates tests that purge or inspect the process-wide query cache. Separate databases do not isolate
/// the interceptor's static counters, generations, or prefetch slot from other test collections.
/// </summary>
[CollectionDefinition(ProcessWideQueryCacheCollection.Name, DisableParallelization = true)]
public sealed class ProcessWideQueryCacheCollection
{
    /// <summary>The collection name shared by cache-state assertions.</summary>
    public const string Name = "Process-wide query cache";
}
