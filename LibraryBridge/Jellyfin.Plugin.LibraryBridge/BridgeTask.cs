using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.LibraryBridge;

public sealed class BridgeTask(BridgeCoordinator coordinator) : IScheduledTask
{
    public string Name => "Refresh Library Bridge";
    public string Key => "RefreshLibraryBridge";
    public string Description => "Refresh cross-library matches and shared progress when enabled; otherwise create a preview.";
    public string Category => "Library";
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        progress.Report(0);
        if (Plugin.Instance?.Configuration.Enabled == true)
            await coordinator.ApplyAsync(false, cancellationToken).ConfigureAwait(false);
        else await coordinator.PreviewAsync(cancellationToken).ConfigureAwait(false);
        progress.Report(100);
    }
}
