using Jellyfin.Plugin.LibraryBridge.Core;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.LibraryBridge;

public sealed class ServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost applicationHost)
    {
        var descriptor = services.LastOrDefault(s => s.ServiceType == typeof(IItemPersistenceService))
            ?? throw new InvalidOperationException("Library Bridge requires Jellyfin's item persistence service.");
        if (descriptor.Lifetime != ServiceLifetime.Singleton)
            throw new InvalidOperationException("Unsupported Jellyfin persistence lifetime; Library Bridge did not activate.");
        services.Remove(descriptor);
        services.AddSingleton<BridgeIndex>();
        var repository = services.LastOrDefault(s => s.ServiceType == typeof(IItemRepository))
            ?? throw new InvalidOperationException("Library Bridge requires Jellyfin's item repository.");
        if (repository.Lifetime != ServiceLifetime.Singleton)
            throw new InvalidOperationException("Unsupported Jellyfin item repository lifetime.");
        services.Remove(repository);
        services.AddSingleton<IItemRepository>(sp => new ResumeRepository(
            (IItemRepository)(repository.ImplementationInstance
                ?? repository.ImplementationFactory?.Invoke(sp)
                ?? ActivatorUtilities.CreateInstance(sp, repository.ImplementationType!)),
            sp.GetRequiredService<BridgeIndex>()));
        services.AddSingleton(_ => new RecoveryJournal(Path.Combine(
            (Plugin.Instance ?? throw new InvalidOperationException("Library Bridge is not initialized.")).DataFolderPath, "recovery-v1.json")));
        services.AddSingleton<IItemPersistenceService>(sp => new GroupingPersistence(
            (IItemPersistenceService)(descriptor.ImplementationInstance
                ?? descriptor.ImplementationFactory?.Invoke(sp)
                ?? ActivatorUtilities.CreateInstance(sp, descriptor.ImplementationType!)),
            sp.GetRequiredService<BridgeIndex>(), sp.GetRequiredService<RecoveryJournal>()));
        services.AddSingleton<Catalog>();
        services.AddSingleton<WatchSynchronizer>();
        services.AddSingleton<BridgeCoordinator>();
        services.AddHttpContextAccessor();
        services.AddSingleton<RequestUser>();
        services.AddSingleton<PlaybackSources>();
        services.AddTransient<BridgeApiFilter>();
        services.AddSingleton<BridgeMediaSourceProvider>();
        services.Configure<MvcOptions>(options => options.Filters.AddService<BridgeApiFilter>());
        services.AddHostedService<BridgeWorker>();
    }
}
