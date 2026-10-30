using Jellyfin.Plugin.LibraryBridge.Core;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Jellyfin.Plugin.LibraryBridge.Tests;

public sealed class RegistrationTests
{
    [Fact] public void HostRegistrationWrapsTheExistingRepositoryWithoutReplacingItsOrdinaryBehavior()
    {
        var expected = new QueryResult<BaseItem>([]);
        var native = StubProxy.Create<IItemRepository>((_, _) => expected);
        var services = new ServiceCollection();
        services.AddSingleton(native);
        services.AddSingleton<IItemPersistenceService>(new MemoryPersistence());
        new ServiceRegistrator().RegisterServices(services, StubProxy.Create<IServerApplicationHost>((_, _) => null));
        using var provider = services.BuildServiceProvider();
        var repository = provider.GetRequiredService<IItemRepository>();
        Assert.IsType<ResumeRepository>(repository);
        Assert.Same(expected, repository.GetItems(new InternalItemsQuery()));
    }

    [Fact] public void PersistenceCanBeResolvedAndUsedBeforePluginConstruction()
    {
        Assert.Null(Plugin.Instance);
        var native = new MemoryPersistence();
        var services = EarlyStartupServices(Path.Combine(Path.GetTempPath(), "BridgeStartup-" + Guid.NewGuid()), native);
        using var provider = services.BuildServiceProvider();

        var persistence = provider.GetRequiredService<IItemPersistenceService>();
        Assert.IsType<GroupingPersistence>(persistence);
        var item = new Movie { Id = Guid.NewGuid(), Name = "Startup", Path = "/media/startup.mkv", PresentationUniqueKey = "original" };
        persistence.SaveItems([item], CancellationToken.None);

        Assert.Equal(1, native.Saves);
        Assert.Equal("original", item.PresentationUniqueKey);
        Assert.Empty(provider.GetRequiredService<RecoveryJournal>().Read());
    }

    [Fact] public void EarlyStartupJournalReadsTheExistingStablePluginDataDirectory()
    {
        Assert.Null(Plugin.Instance);
        var pluginsPath = Path.Combine(Path.GetTempPath(), "BridgeStartup-" + Guid.NewGuid());
        try
        {
            // Match BasePlugin<T>'s assembly-based directory, independent of the versioned install folder.
            var journalPath = Path.Combine(pluginsPath, Path.GetFileNameWithoutExtension(typeof(Plugin).Assembly.Location), "recovery-v1.json");
            var original = new KeyState(Guid.NewGuid(), "/media/movie.mkv", "Movie", "original", null);
            var assignment = new KeyAssignment(original.Id, original.Path, original.Kind, "librarybridge:group", null, "Local", 0);
            new RecoveryJournal(journalPath).Capture([(original, assignment)]);
            using var provider = EarlyStartupServices(pluginsPath, new MemoryPersistence()).BuildServiceProvider();

            Assert.Equal(original, Assert.Single(provider.GetRequiredService<RecoveryJournal>().Read()).Original);
        }
        finally { if (Directory.Exists(pluginsPath)) Directory.Delete(pluginsPath, true); }
    }

    private static ServiceCollection EarlyStartupServices(string pluginsPath, MemoryPersistence persistence)
    {
        var services = new ServiceCollection();
        services.AddSingleton(StubProxy.Create<IApplicationPaths>((method, _) => method.Name == "get_PluginsPath" ? pluginsPath : null));
        services.AddSingleton(StubProxy.Create<IItemRepository>((_, _) => null));
        services.AddSingleton<IItemPersistenceService>(persistence);
        new ServiceRegistrator().RegisterServices(services, StubProxy.Create<IServerApplicationHost>((_, _) => null));
        return services;
    }
}
