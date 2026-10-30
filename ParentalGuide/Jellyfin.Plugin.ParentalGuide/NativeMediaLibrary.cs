using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.ParentalGuide.Core;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.ParentalGuide;

public sealed class NativeMediaLibrary(ILibraryManager library) : IMediaLibrary
{
    public IReadOnlyList<Guid> GetItemIds(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ids = library.GetItemIds(new InternalItemsQuery
        {
            Recursive = true,
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series],
            SourceTypes = [SourceType.Library],
            IsVirtualItem = false,
            OrderBy = [(ItemSortBy.SortName, SortOrder.Ascending), (ItemSortBy.DateCreated, SortOrder.Ascending)]
        });
        cancellationToken.ThrowIfCancellationRequested();
        return ids;
    }

    public MediaTitle? GetItem(Guid id)
    {
        var item = library.GetItemById(id);
        return ToTitle(item);
    }

    private static MediaTitle? ToTitle(BaseItem? item)
    {
        if (item is not (Movie or Series))
        {
            return null;
        }

        item.ProviderIds.TryGetValue("Imdb", out var imdbId);
        imdbId = imdbId?.Trim().ToLowerInvariant();
        return new MediaTitle(item.Id, item.Name, item.ProductionYear, item is Series,
            MetadataRules.ValidImdbId(imdbId) ? imdbId : null, item.OfficialRating, item.Tags.ToArray());
    }

    public async Task ApplyAsync(MediaTitle current, MetadataPlan plan, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var item = library.GetItemById(current.Id) ?? throw new MetadataWriteException("Item was removed.");
        if (ToTitle(item)?.Identity != current.Identity)
        {
            throw new MetadataWriteException("Item identity changed during processing.");
        }

        if (!string.Equals(item.OfficialRating, current.Rating, StringComparison.Ordinal))
        {
            throw new MetadataWriteException("Rating changed during processing.");
        }

        var previousRating = item.OfficialRating;
        var previousTags = item.Tags;
        try
        {
            item.OfficialRating = plan.Rating;
            // Merge into the latest tags, never replace the original collection with external tags.
            item.Tags = item.Tags.Concat(plan.TagsToAdd).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            item.OfficialRating = previousRating;
            item.Tags = previousTags;
            if (error is OperationCanceledException)
            {
                throw;
            }

            throw new MetadataWriteException("Jellyfin could not save the metadata: " + error.GetType().Name);
        }
    }
}
