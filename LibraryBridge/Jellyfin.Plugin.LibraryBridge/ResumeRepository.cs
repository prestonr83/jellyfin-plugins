using System.Reflection;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.LibraryBridge.Core;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;

namespace Jellyfin.Plugin.LibraryBridge;

/// <summary>
/// Resume queries intentionally bypass native presentation grouping. Keep their access, progress,
/// and ordering rules, then collapse only our verified groups before applying the requested page.
/// </summary>
public sealed class ResumeRepository(IItemRepository inner, BridgeIndex index, Func<bool>? enabled = null)
    : IItemRepository
{
    private static readonly PropertyInfo[] QueryOptions = typeof(InternalItemsQuery).GetProperties(BindingFlags.Instance | BindingFlags.Public)
        .Where(property => property.GetMethod?.IsPublic == true && property.SetMethod?.IsPublic == true
            && property.GetIndexParameters().Length == 0 && property.Name != nameof(InternalItemsQuery.DtoOptions)).ToArray();

    private bool Handles(InternalItemsQuery filter) => (enabled?.Invoke() ?? Plugin.Instance?.Configuration.Enabled == true)
        && filter.IsResumable == true && filter.User is not null && filter.GroupByPresentationUniqueKey
        && index.Current.Assignments.Count > 0;

    public QueryResult<BaseItem> GetItems(InternalItemsQuery filter)
    {
        if (!Handles(filter)) return inner.GetItems(filter);
        var items = ResumeCandidates(filter);
        var page = Page(items, filter);
        return new(filter.StartIndex, filter.EnableTotalRecordCount ? items.Count : page.Count, page);
    }

    public IReadOnlyList<BaseItem> GetItemList(InternalItemsQuery filter) => Handles(filter)
        ? Page(ResumeCandidates(filter), filter) : inner.GetItemList(filter);

    public IReadOnlyList<Guid> GetItemIdsList(InternalItemsQuery filter) => Handles(filter)
        ? Page(ResumeCandidates(filter), filter).Select(item => item.Id).ToArray() : inner.GetItemIdsList(filter);

    private IReadOnlyList<BaseItem> ResumeCandidates(InternalItemsQuery filter)
    {
        var snapshot = index.Current;
        // Copy the public query options, including user policy, library boundaries and sorting.
        // Expand paging/exclusions and load provider identities for signature validation.
        // Avoid invoking the runtime's private MemberwiseClone implementation through reflection.
        // The API must retain its original DTO options and requested response fields.
        var candidates = new InternalItemsQuery(filter.User);
        foreach (var property in QueryOptions) property.SetValue(candidates, property.GetValue(filter));
        var dto = filter.DtoOptions;
        candidates.DtoOptions = new DtoOptions(false)
        {
            Fields = dto.Fields.Append(ItemFields.ProviderIds).Distinct().ToArray(),
            ImageTypes = dto.ImageTypes.ToArray(), ImageTypeLimit = dto.ImageTypeLimit,
            EnableImages = dto.EnableImages, EnableUserData = dto.EnableUserData,
            AddCurrentProgram = dto.AddCurrentProgram, AddProgramRecordingInfo = dto.AddProgramRecordingInfo
        };
        candidates.StartIndex = 0;
        candidates.Limit = null;
        candidates.EnableTotalRecordCount = false;
        candidates.ExcludeItemIds = filter.ExcludeItemIds.SelectMany(id => snapshot.Groups.TryGetValue(id, out var group)
            ? group.ItemIds : [id]).Distinct().ToArray();

        // This reads only the user's filtered in-progress items, not their entire media catalog.
        // Native order is retained, including each surviving version's own resume state.
        var result = new List<BaseItem>();
        var seenGroups = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in inner.GetItemList(candidates))
        {
            if (snapshot.Assignments.TryGetValue(item.Id, out var assignment)
                && assignment.PresentationKey is { } key && item.PresentationUniqueKey == key
                && GroupingPersistence.Matches(item, assignment) && !seenGroups.Add(key)) continue;
            result.Add(item);
        }
        return result;
    }

    private static IReadOnlyList<BaseItem> Page(IReadOnlyList<BaseItem> items, InternalItemsQuery filter)
    {
        var page = items.Skip(Math.Max(0, filter.StartIndex ?? 0));
        return (filter.Limit is { } limit ? page.Take(Math.Max(0, limit)) : page).ToArray();
    }

    public BaseItem RetrieveItem(Guid id) => inner.RetrieveItem(id);
    public IReadOnlyList<BaseItem> GetLatestItemList(InternalItemsQuery filter, CollectionType collectionType) => inner.GetLatestItemList(filter, collectionType);
    public Task<bool> ItemExistsAsync(Guid id) => inner.ItemExistsAsync(id);
    public QueryResult<(BaseItem Item, ItemCounts ItemCounts)> GetGenres(InternalItemsQuery filter) => inner.GetGenres(filter);
    public QueryResult<(BaseItem Item, ItemCounts ItemCounts)> GetMusicGenres(InternalItemsQuery filter) => inner.GetMusicGenres(filter);
    public QueryResult<(BaseItem Item, ItemCounts ItemCounts)> GetStudios(InternalItemsQuery filter) => inner.GetStudios(filter);
    public QueryResult<(BaseItem Item, ItemCounts ItemCounts)> GetArtists(InternalItemsQuery filter) => inner.GetArtists(filter);
    public QueryResult<(BaseItem Item, ItemCounts ItemCounts)> GetAllArtists(InternalItemsQuery filter) => inner.GetAllArtists(filter);
    public QueryResult<(BaseItem Item, ItemCounts ItemCounts)> GetAlbumArtists(InternalItemsQuery filter) => inner.GetAlbumArtists(filter);
    public IReadOnlyList<string> GetMusicGenreNames() => inner.GetMusicGenreNames();
    public IReadOnlyList<string> GetStudioNames() => inner.GetStudioNames();
    public IReadOnlyList<string> GetGenreNames() => inner.GetGenreNames();
    public IReadOnlyList<string> GetMediaStreamLanguages(InternalItemsQuery filter, MediaStreamType mediaStreamType) => inner.GetMediaStreamLanguages(filter, mediaStreamType);
    public IReadOnlyList<string> GetTagNames(InternalItemsQuery filter) => inner.GetTagNames(filter);
    public IReadOnlyList<string> GetAllArtistNames() => inner.GetAllArtistNames();
    public QueryFiltersLegacy GetQueryFiltersLegacy(InternalItemsQuery filter) => inner.GetQueryFiltersLegacy(filter);
    public bool GetIsPlayed(User user, Guid id, bool recursive) => inner.GetIsPlayed(user, id, recursive);
}
