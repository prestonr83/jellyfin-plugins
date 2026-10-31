using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.LibraryBridge.Core;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Dto;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.LibraryBridge;

public sealed class RequestUser(IHttpContextAccessor http, IAuthorizationContext authorization, IUserManager users)
{
    public async Task<User?> ResolveAsync()
    {
        var context = http.HttpContext;
        if (context is null) return null;
        var auth = await authorization.GetAuthorizationInfo(context).ConfigureAwait(false);
        if (!auth.IsAuthenticated) return null;
        if (auth.User is not null) return auth.User;
        // A userless API key is not a user visibility policy. Require an explicit user on that request.
        if (auth.IsApiKey && Guid.TryParse(context.Request.Query["userId"], out var id)) return users.GetUserById(id);
        return null;
    }
}

public sealed class PlaybackSources(ILibraryManager library, BridgeIndex index, Func<bool>? enabled = null)
{
    public bool Enabled => enabled?.Invoke() ?? Plugin.Instance?.Configuration.Enabled == true;
    public Guid EpisodeQueueStart(Guid startId, Guid seriesId, User user)
    {
        var snapshot = index.Current;
        if (!Enabled || !snapshot.Groups.TryGetValue(startId, out var group) || group.Kind != "Episode"
            || !CanSelect(startId, startId, user)
            || library.GetItemById<Episode>(startId, user) is not { } episode
            || !snapshot.Groups.TryGetValue(episode.SeriesId, out var seriesGroup) || seriesGroup.Kind != "Series"
            || !seriesGroup.ItemIds.Contains(seriesId)
            || library.GetItemById<Series>(seriesId, user) is not { } series
            || !snapshot.Assignments.TryGetValue(seriesId, out var seriesAssignment)
            || !GroupingPersistence.Matches(series, seriesAssignment)) return startId;

        // GetEpisodes skips everything until this exact ID. Its grouped listing may keep a
        // different copy, so ask the native, user-filtered query which ID survives grouping.
        // The client retains the original selected source when the returned row has another ID.
        var ids = group.ItemIds.Where(id => CanSelect(startId, id, user)).ToArray();
        if (ids.Length == 0) return startId;
        var starts = library.GetItemIds(new InternalItemsQuery(user)
        {
            ItemIds = ids, IncludeItemTypes = [BaseItemKind.Episode],
            SeriesPresentationUniqueKey = episode.SeriesPresentationUniqueKey,
            GroupByPresentationUniqueKey = true,
            IsMissing = user.DisplayMissingEpisodes ? null : false
        });
        return starts.Count == 1 && ids.Contains(starts[0]) ? starts[0] : startId;
    }
    public bool CanSelect(Guid displayedId, Guid selectedId, User user)
    {
        var snapshot = index.Current;
        if (!Enabled || !snapshot.Groups.TryGetValue(displayedId, out var group) || group.Kind == "Series"
            || !group.ItemIds.Contains(selectedId)) return false;
        return Matches(displayedId) && Matches(selectedId);
        bool Matches(Guid id) => library.GetItemById<Video>(id, user) is { } item
            && snapshot.Assignments.TryGetValue(id, out var assignment) && GroupingPersistence.Matches(item, assignment);
    }
    public IReadOnlyList<MediaSourceInfo> Get(BaseItem item, User user, bool peersOnly)
    {
        var snapshot = index.Current;
        if (!Enabled || !snapshot.Groups.TryGetValue(item.Id, out var group) || group.Kind == "Series"
            || library.GetItemById<BaseItem>(item.Id, user) is null
            || !snapshot.Assignments.TryGetValue(item.Id, out var displayedAssignment)
            || !GroupingPersistence.Matches(item, displayedAssignment)) return [];
        var sources = new List<MediaSourceInfo>();
        foreach (var id in group.ItemIds)
        {
            if (peersOnly && id == item.Id) continue;
            var other = library.GetItemById<Video>(id, user);
            if (other is null || !snapshot.Assignments.TryGetValue(id, out var assignment)
                || !GroupingPersistence.Matches(other, assignment)) continue;
            foreach (var source in other.GetMediaSources(false))
            {
                // Existing native alternate groups are excluded from the catalog; do not expand one accidentally.
                if (!Guid.TryParse(source.Id, out var sourceId) || sourceId != other.Id) continue;
                Label(source, assignment.Label);
                sources.Add(source);
            }
        }
        return sources.DistinctBy(s => s.Id).ToArray();
    }
    public void Label(IEnumerable<MediaSourceInfo> sources)
    {
        var snapshot = index.Current;
        foreach (var source in sources)
            if (Guid.TryParse(source.Id, out var id) && snapshot.Assignments.TryGetValue(id, out var assignment)) Label(source, assignment.Label);
    }
    private static void Label(MediaSourceInfo source, string label)
    {
        if (source.Name != label && !(source.Name?.StartsWith(label + " · ", StringComparison.Ordinal) ?? false))
            source.Name = string.IsNullOrWhiteSpace(source.Name) ? label : label + " · " + source.Name;
    }
}

public sealed class BridgeMediaSourceProvider(PlaybackSources sources, RequestUser requestUser) : IMediaSourceProvider
{
    public async Task<IEnumerable<MediaSourceInfo>> GetMediaSources(BaseItem item, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var user = await requestUser.ResolveAsync().ConfigureAwait(false);
        return user is null ? [] : sources.Get(item, user, true);
    }
    public Task<ILiveStream> OpenMediaSource(string openToken, List<ILiveStream> currentLiveStreams, CancellationToken cancellationToken)
        => throw new NotSupportedException("Library Bridge returns ordinary native file/STRM sources which do not require opening.");
}
