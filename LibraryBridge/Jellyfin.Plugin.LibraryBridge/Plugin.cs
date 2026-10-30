using Jellyfin.Plugin.LibraryBridge.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.LibraryBridge;

public sealed class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public static readonly Guid PluginId = new("1c550a63-a1f1-446a-8f69-b7fedc04c384");
    public Plugin(IApplicationPaths paths, IXmlSerializer xml) : base(paths, xml) => Instance = this;
    public static Plugin? Instance { get; private set; }
    public override Guid Id => PluginId;
    public override string Name => "Library Bridge";
    public override string Description => "One search, Next Up and Continue Watching entry across paired local and on-demand libraries, with selectable playback sources.";
    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new() { Name = "LibraryBridge", EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html" };
    }
}
