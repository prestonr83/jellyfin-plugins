using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.LibraryBridge.Core;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Xunit;

namespace Jellyfin.Plugin.LibraryBridge.Tests;

public sealed class EpisodeQueueTests
{
    [Fact] public async Task QueueLookupUsesTheNativeRepresentativeButPlaybackKeepsTheSelectedLocalCopy()
    {
        var graph = new Graph();
        graph.Http.Request.Path = "/Shows/" + graph.Series.Id + "/Episodes";
        var args = new Dictionary<string, object?> { ["seriesId"] = graph.Series.Id, ["startItemId"] = graph.Local.Id, ["limit"] = 100 };
        await graph.Run(args);
        Assert.Equal(graph.Demand.Id, args["startItemId"]);
        Assert.Equal(100, args["limit"]);
        var query = Assert.Single(graph.Queries);
        Assert.Same(graph.User, query.User);
        Assert.True(query.GroupByPresentationUniqueKey);
        Assert.Equal("series", query.SeriesPresentationUniqueKey);
        Assert.Equal(new[] { BaseItemKind.Episode }, query.IncludeItemTypes);
        Assert.Equal(new[] { graph.Local.Id, graph.Demand.Id }, query.ItemIds);

        graph.Http.Request.Path = "/Items/" + graph.Demand.Id + "/PlaybackInfo";
        args = new() { ["itemId"] = graph.Demand.Id, ["mediaSourceId"] = graph.Local.Id.ToString("N") };
        await graph.Run(args);
        Assert.Equal(graph.Local.Id, args["itemId"]);
    }

    [Theory] [InlineData("restricted-start")] [InlineData("restricted-series")] [InlineData("changed-start")]
    [InlineData("changed-series")] [InlineData("unrelated-series")] [InlineData("disabled")]
    public void InvalidOrInaccessibleQueuesAreDelegatedUnchanged(string scenario)
    {
        var graph = new Graph(); var seriesId = graph.Series.Id;
        switch (scenario)
        {
            case "restricted-start": graph.Denied.Add(graph.Local.Id); break;
            case "restricted-series": graph.Denied.Add(seriesId); break;
            case "changed-start": graph.Local.Path += ".changed"; break;
            case "changed-series": graph.Series.Path += ".changed"; break;
            case "unrelated-series": seriesId = Guid.NewGuid(); break;
            case "disabled": graph.Enabled = false; break;
        }
        Assert.Equal(graph.Local.Id, graph.Sources.EpisodeQueueStart(graph.Local.Id, seriesId, graph.User));
        Assert.Empty(graph.Queries);
    }

    [Fact] public void NativeLookupNeverIncludesRestrictedAlternateCopies()
    {
        var graph = new Graph(); graph.Denied.Add(graph.Demand.Id);
        Assert.Equal(graph.Local.Id, graph.Sources.EpisodeQueueStart(graph.Local.Id, graph.Series.Id, graph.User));
        Assert.Equal(new[] { graph.Local.Id }, Assert.Single(graph.Queries).ItemIds);
    }

    [Fact] public async Task UnauthenticatedQueueRequestsAreUnchanged()
    {
        var graph = new Graph(); graph.Authenticated = false; graph.Http.Request.Path = "/Shows/x/Episodes";
        var args = new Dictionary<string, object?> { ["seriesId"] = graph.Series.Id, ["startItemId"] = graph.Local.Id };
        await graph.Run(args);
        Assert.Equal(graph.Local.Id, args["startItemId"]); Assert.Empty(graph.Queries);
    }

    private sealed class Graph
    {
        public User User { get; } = new("test", "auth", "reset");
        public Series Series { get; } = new() { Id = Guid.NewGuid(), Path = "/local/show", PresentationUniqueKey = "series" };
        public Series OtherSeries { get; } = new() { Id = Guid.NewGuid(), Path = "/demand/show", PresentationUniqueKey = "series" };
        public Episode Local { get; } = new() { Id = Guid.NewGuid(), Path = "/local/show/episode.mkv", SeriesPresentationUniqueKey = "series" };
        public Episode Demand { get; } = new() { Id = Guid.NewGuid(), Path = "/demand/show/episode.strm", SeriesPresentationUniqueKey = "series" };
        public HashSet<Guid> Denied { get; } = [];
        public List<InternalItemsQuery> Queries { get; } = [];
        public DefaultHttpContext Http { get; } = new();
        public bool Enabled { get; set; } = true;
        public bool Authenticated { get; set; } = true;
        public PlaybackSources Sources { get; }
        private BridgeApiFilter Filter { get; }
        public Graph()
        {
            Local.SeriesId = Series.Id; Demand.SeriesId = OtherSeries.Id;
            BaseItem[] items = [Series, OtherSeries, Local, Demand];
            var index = new BridgeIndex();
            index.Publish(new(items.Select(item => new KeyAssignment(item.Id, item.Path, GroupingPersistence.Kind(item),
                item is Series ? "series" : "episode", item is Episode ? "series" : null, "", 0)).ToArray(),
                [new("series", "Series", "Example", [Series.Id, OtherSeries.Id]), new("episode", "Episode", "Example", [Local.Id, Demand.Id])], []));
            var library = StubProxy.Create<ILibraryManager>((method, args) =>
            {
                if (method.Name == "GetItemById" && args?[0] is Guid id)
                    return Denied.Contains(id) ? null : items.SingleOrDefault(item => item.Id == id);
                if (method.Name == "GetItemIds" && args?[0] is InternalItemsQuery query)
                { Queries.Add(query); return query.ItemIds.Contains(Demand.Id) ? new[] { Demand.Id } : new[] { Local.Id }; }
                return null;
            });
            Sources = new(library, index, () => Enabled);
            var authorization = StubProxy.Create<IAuthorizationContext>((_, _) => Task.FromResult(new AuthorizationInfo { IsAuthenticated = Authenticated, User = User }));
            var requestUser = new RequestUser(new HttpContextAccessor { HttpContext = Http }, authorization, StubProxy.Create<IUserManager>((_, _) => User));
            Filter = new(Sources, requestUser, library, index);
        }
        public Task Run(Dictionary<string, object?> arguments)
        {
            var action = new ActionContext(Http, new RouteData(), new ActionDescriptor());
            return Filter.OnActionExecutionAsync(new(action, [], arguments, new object()),
                () => Task.FromResult(new ActionExecutedContext(action, [], new object())));
        }
    }
}
