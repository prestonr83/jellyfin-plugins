using Jellyfin.Data.Enums;
using Jellyfin.Plugin.LibraryBridge.Configuration;
using Jellyfin.Plugin.LibraryBridge.Core;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;

namespace Jellyfin.Plugin.LibraryBridge;

public sealed class Catalog(ILibraryManager library)
{
    private const int MetadataBatchSize = 128;
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
            var primaryCount = Read(primary, pair.PrimaryLabel, 0);
            var alternateCount = Read(alternate, pair.AlternateLabel, 1);
            var plan = PlanBuilder.Build(sides, $"{primary:N}:{alternate:N}:{pair.ContentType}");
            var warnings = plan.Warnings.ToList();
            if (primaryCount == 0) Empty(pair.PrimaryLibrary);
            if (alternateCount == 0) Empty(pair.AlternateLibrary);
            if (plan.Groups.Length == 0)
                warnings.Add($"No cross-library {pair.ContentType} matches for '{pair.PrimaryLibrary}' and '{pair.AlternateLibrary}' "
                    + $"(Primary: {primaryCount}; Alternate: {alternateCount}). Matching titles must share valid TMDb, TVDb or IMDb provider IDs "
                    + "and compatible metadata; existing version groups are excluded.");
            plans.Add(plan with { Warnings = warnings.ToArray() });

            void Empty(string name) => warnings.Add($"Library '{name}' returned no scanned {pair.ContentType} titles. Check the content type and library scan.");

            Guid Resolve(string name)
            {
                var matches = folders.Where(f => string.Equals(f.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase)
                    || string.Equals(f.ItemId, name?.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
                if (matches.Length != 1 || !Guid.TryParse(matches[0].ItemId, out var id))
                    throw new InvalidOperationException($"Library '{name}' must identify exactly one existing library by name or ID.");
                return id;
            }
            int Read(Guid id, string label, int preference)
            {
                var parent = library.GetItemById(id) as CollectionFolder
                    ?? throw new InvalidOperationException("Library no longer exists or is not a collection folder.");
                var physicalRoots = parent.PhysicalFolderIds.Where(root => root != Guid.Empty).Distinct().ToArray();
                if (physicalRoots.Length == 0) return 0;
                var kinds = pair.ContentType == "Movie" ? new[] { BaseItemKind.Movie }
                    : new[] { BaseItemKind.Series, BaseItemKind.Season, BaseItemKind.Episode };
                // Match Jellyfin's collection-folder expansion explicitly for the ID-only query.
                // Hydrate bounded batches without image/user-data joins instead of buffering an
                // entire on-demand library through the provider's full-metadata reader/cache.
                var itemIds = library.GetItemIds(new InternalItemsQuery
                {
                    TopParentIds = physicalRoots, Recursive = true, IncludeItemTypes = kinds,
                    GroupByPresentationUniqueKey = false, IncludeAlternateVersions = true,
                    SourceTypes = [SourceType.Library], IsVirtualItem = false
                });
                var titleCount = 0;
                foreach (var batch in itemIds.Distinct().Chunk(MetadataBatchSize))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var items = library.GetItemList(new InternalItemsQuery
                    {
                        ItemIds = batch, TopParentIds = physicalRoots, IncludeItemTypes = kinds,
                        GroupByPresentationUniqueKey = false, IncludeAlternateVersions = true,
                        SourceTypes = [SourceType.Library], IsVirtualItem = false,
                        DtoOptions = new DtoOptions(false)
                        {
                            Fields = [ItemFields.ProviderIds], EnableImages = false, EnableUserData = false
                        }
                    });
                    foreach (var item in items)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (string.IsNullOrEmpty(item.Path)) continue;
                        if (GroupingPersistence.Kind(item) == pair.ContentType) titleCount++;
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
                return titleCount;
            }
        }
        return new(plans.SelectMany(p => p.Assignments).ToArray(), plans.SelectMany(p => p.Groups).ToArray(), plans.SelectMany(p => p.Warnings).ToArray());
    }
}
