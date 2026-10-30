using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.LibraryBridge.Core;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.LibraryBridge.Tests;

public sealed class WatchSynchronizerTests
{
    [Fact] public void WatchingEitherSourceUpdatesTheOtherAndKeepsVersionPreferences()
    {
        using var graph = new Graph();
        var target = graph.Data[graph.B.Id]; target.AudioStreamIndex = 3; target.SubtitleStreamIndex = 7; target.IsFavorite = true; target.Rating = 8;
        graph.Publish(new() { Key = "source", Played = false, PlaybackPositionTicks = 100, PlayCount = 2, LastPlayedDate = DateTime.UtcNow });
        var result = graph.Data[graph.B.Id];
        Assert.Equal(200, result.PlaybackPositionTicks); Assert.Equal(2, result.PlayCount);
        Assert.Equal(3, result.AudioStreamIndex); Assert.Equal(7, result.SubtitleStreamIndex); Assert.True(result.IsFavorite); Assert.Equal(8, result.Rating);
        Assert.Equal(1, graph.Saves);
    }
    [Fact] public void MarkingUnwatchedPropagatesWithoutBeingReplacedByOldWatchedState()
    {
        using var graph = new Graph(); graph.Data[graph.B.Id].Played = true;
        graph.Publish(new() { Key = "source", Played = false, PlaybackPositionTicks = 0, PlayCount = 0 }, UserDataSaveReason.TogglePlayed);
        Assert.False(graph.Data[graph.B.Id].Played);
    }
    [Fact] public void SynchronizationCannotWriteToAnInaccessibleLibrary()
    {
        using var graph = new Graph(); graph.Denied.Add(graph.B.Id);
        graph.Publish(new() { Key = "source", Played = true, PlayCount = 1 });
        Assert.False(graph.Data[graph.B.Id].Played); Assert.Equal(0, graph.Saves);
    }
    [Fact] public void ReconciliationPreservesExistingCompletedHistory()
    {
        using var graph = new Graph(); graph.Data[graph.A.Id].Played = true; graph.Data[graph.A.Id].PlayCount = 1;
        graph.Synchronizer.Reconcile(graph.Plan, default);
        Assert.True(graph.Data[graph.B.Id].Played); Assert.Equal(1, graph.Data[graph.B.Id].PlayCount);
    }
    [Fact] public void FavoriteOnlyEventsDoNotOverwriteWatchState()
    {
        using var graph = new Graph();
        graph.Publish(new() { Key = "source", Played = true }, UserDataSaveReason.UpdateUserRating);
        Assert.Equal(0, graph.Saves);
    }

    private sealed class Graph : IDisposable
    {
        public User User { get; } = new("watch", "auth", "reset");
        public Movie A { get; } = new() { Id = Guid.NewGuid(), RunTimeTicks = 1000 };
        public Movie B { get; } = new() { Id = Guid.NewGuid(), RunTimeTicks = 2000 };
        public HashSet<Guid> Denied { get; } = [];
        public Dictionary<Guid, UserItemData> Data { get; }
        private event EventHandler<UserDataSaveEventArgs>? Saved;
        public int Saves { get; private set; }
        public WatchSynchronizer Synchronizer { get; }
        public GroupingPlan Plan { get; }
        public Graph()
        {
            Data = new() { [A.Id] = new() { Key = "a" }, [B.Id] = new() { Key = "b" } };
            Plan = new([], [new("g", "Movie", "Example", [A.Id, B.Id])], []);
            var index = new BridgeIndex(); index.Publish(Plan);
            var library = StubProxy.Create<ILibraryManager>((_, args) => args?[0] is Guid id && !Denied.Contains(id) ? id == A.Id ? A : id == B.Id ? B : null : null);
            var users = StubProxy.Create<IUserManager>((method, _) => method.Name == "GetUsers" ? new[] { User } : User);
            var data = StubProxy.Create<IUserDataManager>((method, args) =>
            {
                switch (method.Name)
                {
                    case "add_UserDataSaved": Saved += (EventHandler<UserDataSaveEventArgs>)args![0]!; return null;
                    case "remove_UserDataSaved": Saved -= (EventHandler<UserDataSaveEventArgs>)args![0]!; return null;
                    case "GetUserData": return Data[((BaseItem)args![1]!).Id];
                    case "SaveUserData":
                        var item = (BaseItem)args![1]!; var value = (UserItemData)args[2]!;
                        Data[item.Id] = value; Saves++;
                        Saved?.Invoke(this, new() { Item = item, UserId = User.Id, UserData = value, SaveReason = (UserDataSaveReason)args[3]! });
                        return null;
                }
                return null;
            });
            Synchronizer = new(library, users, data, index, NullLogger<WatchSynchronizer>.Instance, () => true);
            Synchronizer.Subscribe();
        }
        public void Publish(UserItemData state, UserDataSaveReason reason = UserDataSaveReason.PlaybackProgress)
        {
            Data[A.Id] = state; Saved?.Invoke(this, new() { Item = A, UserId = User.Id, UserData = state, SaveReason = reason });
        }
        public void Dispose() => Synchronizer.Dispose();
    }
}
