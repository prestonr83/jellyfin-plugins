using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LibraryBridge;

public sealed class BridgeWorker(BridgeCoordinator coordinator, WatchSynchronizer watch, ILibraryManager library,
    ILogger<BridgeWorker> logger) : BackgroundService
{
    private int _dirty = 1;
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        watch.Subscribe();
        library.ItemAdded += Changed;
        library.ItemUpdated += Changed;
        library.ItemRemoved += Changed;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken).ConfigureAwait(false);
            var last = DateTime.MinValue;
            while (!stoppingToken.IsCancellationRequested)
            {
                var enabled = Plugin.Instance?.Configuration.Enabled == true;
                var minutes = Math.Clamp(Plugin.Instance?.Configuration.RefreshMinutes ?? 15, 1, 1440);
                if (Interlocked.Exchange(ref _dirty, 0) == 1 || DateTime.UtcNow - last >= TimeSpan.FromMinutes(minutes))
                {
                    try
                    {
                        if (enabled) await coordinator.ApplyAsync(false, stoppingToken).ConfigureAwait(false);
                        else await coordinator.RestoreAsync(stoppingToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                    catch (Exception error) { logger.LogError(error, "Library Bridge refresh stopped; see plugin status for recovery details"); }
                    last = DateTime.UtcNow;
                }
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false);
            }
        }
        finally
        {
            library.ItemAdded -= Changed;
            library.ItemUpdated -= Changed;
            library.ItemRemoved -= Changed;
            watch.Dispose();
        }
    }
    private void Changed(object? sender, ItemChangeEventArgs args) => Interlocked.Exchange(ref _dirty, 1);
}
