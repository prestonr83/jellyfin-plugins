using Jellyfin.Data.Enums;
using Jellyfin.Plugin.LibraryBridge.Configuration;
using Jellyfin.Plugin.LibraryBridge.Core;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.LibraryBridge;

public sealed class Catalog(ILibraryManager library)
{
    public GroupingPlan Build(PluginConfiguration configuration, CancellationToken cancellationToken)
    {
        if (configuration.LibraryPairs is null || configuration.LibraryPairs.Length == 0)
            throw new InvalidOperationException("Configure at least one library pair.");
        var folders = library.GetVirtualFolders();
        var plans = new List<GroupingPlan>();
        var usedLibraries = new HashSet<Guid>();
        foreach (var pair in configuration.LibraryPairs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (pair.ContentType is not ("Movie" or "Series")) throw new InvalidOperationException("ContentType must be Movie or Series.");
            var sides = new List<CatalogItem>();
            var primary = Resolve(pair.PrimaryLibrary);
            var alternate = Resolve(pair.AlternateLibrary);
            if (!usedLibraries.Add(primary) || !usedLibraries.Add(alternate))
                throw new InvalidOperationException("Each library may appear in only one pair.");
            Read(primary, pair.PrimaryLabel, 0);
            Read(alternate, pair.AlternateLabel, 1);
            plans.Add(PlanBuilder.Build(sides, $"{primary:N}:{alternate:N}:{pair.ContentType}"));

            Guid Resolve(string name)
            {
                var matches = folders.Where(f => string.Equals(f.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase)
                    || string.Equals(f.ItemId, name?.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
                if (matches.Length != 1 || !Guid.TryParse(matches[0].ItemId, out var id))
                    throw new InvalidOperationException($"Library '{name}' must identify exactly one existing library by name or ID.");
                return id;
            }
            void Read(Guid id, string label, int preference)
            {
                var parent = library.GetItemById(id) ?? throw new InvalidOperationException("Library no longer exists.");
                var kinds = pair.ContentType == "Movie" ? new[] { BaseItemKind.Movie }
                    : new[] { BaseItemKind.Series, BaseItemKind.Season, BaseItemKind.Episode };
                var itemIds = library.GetItemIds(new InternalItemsQuery
                {
                    Parent = parent, Recursive = true, IncludeItemTypes = kinds,
                    GroupByPresentationUniqueKey = false, IncludeAlternateVersions = true,
                    SourceTypes = [SourceType.Library], IsVirtualItem = false
                });
                foreach (var itemId in itemIds)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var item = library.GetItemById(itemId);
                    if (item is null || string.IsNullOrEmpty(item.Path)) continue;
                    var eligible = item is not Video video || (!video.PrimaryVersionId.HasValue
                        && video.LinkedAlternateVersions.Length == 0 && video.LocalAlternateVersions.Length == 0
                        && video.OwnerId == Guid.Empty);
                    sides.Add(new(item.Id, GroupingPersistence.Kind(item), item.Name, item.Path, id,
                        string.IsNullOrWhiteSpace(label) ? (preference == 0 ? "Local" : "On Demand") : label.Trim(), preference,
                        new Dictionary<string, string>(item.ProviderIds, StringComparer.OrdinalIgnoreCase),
                        (item as IHasSeries)?.SeriesId ?? Guid.Empty,
                        item is Episode episode ? episode.ParentIndexNumber : item.IndexNumber,
                        item is Episode ep ? ep.IndexNumber : null,
                        (item as Episode)?.IndexNumberEnd,
                        (item as Series)?.DisplayOrder?.Trim() ?? "", eligible));
                }
            }
        }
        return new(plans.SelectMany(p => p.Assignments).ToArray(), plans.SelectMany(p => p.Groups).ToArray(), plans.SelectMany(p => p.Warnings).ToArray());
    }
}
