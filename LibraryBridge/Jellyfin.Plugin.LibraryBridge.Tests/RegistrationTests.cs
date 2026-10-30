using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
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
}
