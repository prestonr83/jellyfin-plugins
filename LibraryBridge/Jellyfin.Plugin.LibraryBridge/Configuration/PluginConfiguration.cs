using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.LibraryBridge.Configuration;

public sealed class PluginConfiguration : BasePluginConfiguration
{
    public bool Enabled { get; set; }
    public bool SynchronizeWatchState { get; set; } = true;
    public bool ReconcileExistingWatchState { get; set; } = true;
    public int RefreshMinutes { get; set; } = 15;
    public LibraryPair[] LibraryPairs { get; set; } =
    [
        new() { PrimaryLibrary = "Shows", AlternateLibrary = "Shows On Demand", ContentType = "Series" },
        new() { PrimaryLibrary = "Movies", AlternateLibrary = "Movies On Demand", ContentType = "Movie" }
    ];
}

public sealed class LibraryPair
{
    public string PrimaryLibrary { get; set; } = "";
    public string AlternateLibrary { get; set; } = "";
    public string ContentType { get; set; } = "Series";
    public string PrimaryLabel { get; set; } = "Local";
    public string AlternateLabel { get; set; } = "On Demand";
}
