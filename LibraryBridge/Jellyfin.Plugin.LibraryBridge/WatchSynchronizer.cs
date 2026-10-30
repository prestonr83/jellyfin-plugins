using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.LibraryBridge.Core;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LibraryBridge;

public sealed class WatchSynchronizer(ILibraryManager library, IUserManager users, IUserDataManager userData,
    BridgeIndex index, ILogger<WatchSynchronizer> logger, Func<bool>? enabled = null) : IDisposable
{
    private readonly object _gate = new();
    private readonly AsyncLocal<bool> _saving = new();
    private bool _subscribed;
    public void Subscribe()
    {
        if (_subscribed) return;
        userData.UserDataSaved += OnSaved;
        _subscribed = true;
    }
    public void Dispose()
    {
        if (_subscribed) userData.UserDataSaved -= OnSaved;
        _subscribed = false;
    }
    private void OnSaved(object? sender, UserDataSaveEventArgs args)
    {
        if (_saving.Value || !(enabled?.Invoke() ?? Plugin.Instance?.Configuration is { Enabled: true, SynchronizeWatchState: true })
            || args.SaveReason == UserDataSaveReason.UpdateUserRating) return;
        if (!index.Current.Groups.TryGetValue(args.Item.Id, out var group) || group.Kind == "Series") return;
        var user = users.GetUserById(args.UserId);
        if (user is null || library.GetItemById<BaseItem>(args.Item.Id, user) is null) return;
        lock (_gate)
        {
            _saving.Value = true;
            try
            {
                var state = Read(args.UserData);
                foreach (var id in group.ItemIds.Where(id => id != args.Item.Id))
                {
                    var target = library.GetItemById<BaseItem>(id, user);
                    if (target is not null) Save(user, target, state, args.Item.RunTimeTicks, args.SaveReason, CancellationToken.None);
                }
            }
            catch (Exception error) { logger.LogError(error, "Library Bridge could not synchronize playback for {ItemId}", args.Item.Id); }
            finally { _saving.Value = false; }
        }
    }
    public void Reconcile(GroupingPlan plan, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _saving.Value = true;
            try
            {
                foreach (var user in users.GetUsers())
                foreach (var group in plan.Groups.Where(g => g.Kind is "Movie" or "Episode"))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var accessible = group.ItemIds.Select(id => library.GetItemById<BaseItem>(id, user)).OfType<BaseItem>().ToArray();
                    if (accessible.Length < 2) continue;
                    var data = accessible.Select(item => (Item: item, State: Read(userData.GetUserData(user, item)))).ToArray();
                    var chosen = WatchState.Reconcile(data.Select(d => d.State));
                    var duration = data.OrderByDescending(d => d.State.LastPlayedDate).ThenByDescending(d => d.State.PositionTicks).First().Item.RunTimeTicks;
                    foreach (var item in accessible) Save(user, item, chosen, duration, UserDataSaveReason.Import, cancellationToken);
                }
            }
            finally { _saving.Value = false; }
        }
    }
    private static WatchState Read(UserItemData? data) => data is null ? new(false, 0, 0, null)
        : new(data.Played, data.PlaybackPositionTicks, data.PlayCount, data.LastPlayedDate);
    private void Save(User user, BaseItem item, WatchState state, long? sourceDuration, UserDataSaveReason reason, CancellationToken ct)
    {
        var current = userData.GetUserData(user, item);
        if (current is null) return;
        var position = state.PositionFor(sourceDuration, item.RunTimeTicks);
        if (current.Played == state.Played && current.PlaybackPositionTicks == position
            && current.PlayCount == state.PlayCount && current.LastPlayedDate == state.LastPlayedDate) return;
        // Audio/subtitle indexes, favorites and ratings belong to this version and stay intact.
        var replacement = new UserItemData
        {
            Key = current.Key, Played = state.Played, PlaybackPositionTicks = position,
            PlayCount = state.PlayCount, LastPlayedDate = state.LastPlayedDate,
            AudioStreamIndex = current.AudioStreamIndex, SubtitleStreamIndex = current.SubtitleStreamIndex,
            IsFavorite = current.IsFavorite, Rating = current.Rating
        };
        userData.SaveUserData(user, item, replacement, reason, ct);
    }
}
