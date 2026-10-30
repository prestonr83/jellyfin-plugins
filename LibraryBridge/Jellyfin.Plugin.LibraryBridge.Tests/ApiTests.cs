using System.Reflection;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.LibraryBridge.Api;
using Jellyfin.Plugin.LibraryBridge.Core;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Session;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Xunit;

namespace Jellyfin.Plugin.LibraryBridge.Tests;

public sealed class ApiTests
{
    [Fact] public void VersionListContainsBothNativeSourcesWithClearLabels()
    {
        var graph = new Graph();
        var sources = graph.Sources.Get(graph.Local, graph.User, false);
        Assert.Equal(2, sources.Count);
        Assert.StartsWith("Local · ", sources[0].Name); Assert.StartsWith("On Demand · ", sources[1].Name);
        Assert.Equal(graph.Demand.Id.ToString("N"), sources[1].Id);
    }
    [Fact] public void RestrictedLibrarySourcesAreNotAdvertised()
    {
        var graph = new Graph(); graph.Denied.Add(graph.Demand.Id);
        Assert.Single(graph.Sources.Get(graph.Local, graph.User, false));
        Assert.Empty(graph.Sources.Get(graph.Local, graph.User, true));
    }
    [Fact] public void AnInaccessibleDisplayedItemCannotExposeAnySources()
    {
        var graph = new Graph(); graph.Denied.Add(graph.Local.Id);
        Assert.Empty(graph.Sources.Get(graph.Local, graph.User, false));
    }
    [Fact] public void DisabledSourcesReturnNothing()
    {
        var graph = new Graph();
        Assert.Empty(new PlaybackSources(graph.Library, graph.Index, () => false).Get(graph.Local, graph.User, false));
    }
    [Fact] public void ReidentifyingTheDisplayedTitleImmediatelyStopsAlternateExpansion()
    {
        var graph = new Graph(true); graph.Local.ProviderIds["Tmdb"] = "2";
        Assert.Empty(graph.Sources.Get(graph.Local, graph.User, true));
    }
    [Fact] public void ReidentifyingEitherTitleImmediatelyBlocksStaleVersionSelection()
    {
        var graph = new Graph(true);
        Assert.True(graph.Sources.CanSelect(graph.Local.Id, graph.Demand.Id, graph.User));
        graph.Demand.ProviderIds["Tmdb"] = "2";
        Assert.False(graph.Sources.CanSelect(graph.Local.Id, graph.Demand.Id, graph.User));
    }
    [Fact] public async Task DynamicProviderSuppliesOnlyTheOtherVersion()
    {
        var graph = new Graph();
        var provider = new BridgeMediaSourceProvider(graph.Sources, graph.RequestUser);
        var source = Assert.Single(await provider.GetMediaSources(graph.Local, default));
        Assert.Equal(graph.Demand.Id.ToString("N"), source.Id);
    }
    [Fact] public async Task UnauthenticatedRequestsDoNotGainAlternateSources()
    {
        var graph = new Graph(); graph.Authenticated = false;
        Assert.Empty(await new BridgeMediaSourceProvider(graph.Sources, graph.RequestUser).GetMediaSources(graph.Local, default));
    }
    [Fact] public async Task DetailDtoAdvertisesNativeVersionChooser()
    {
        var graph = new Graph(); var dto = new BaseItemDto { Id = graph.Local.Id, MediaSources = [] };
        var result = new ObjectResult(dto);
        var action = new ActionContext(graph.Http, new RouteData(), new ActionDescriptor());
        var context = new ResultExecutingContext(action, [], result, new object());
        await graph.Filter.OnResultExecutionAsync(context, () => Task.FromResult(new ResultExecutedContext(action, [], result, new object())));
        Assert.Equal(2, dto.MediaSourceCount); Assert.Equal(2, dto.MediaSources.Length);
    }
    [Fact] public async Task PlaybackNegotiationTargetsTheSelectedStrmForProbing()
    {
        var graph = new Graph(); graph.Http.Request.Path = "/Items/" + graph.Local.Id + "/PlaybackInfo";
        var action = new ActionContext(graph.Http, new RouteData(), new ActionDescriptor());
        var args = new Dictionary<string, object?> { ["itemId"] = graph.Local.Id, ["mediaSourceId"] = graph.Demand.Id.ToString("N") };
        var context = new ActionExecutingContext(action, [], args, new object());
        await graph.Filter.OnActionExecutionAsync(context, () => Task.FromResult(new ActionExecutedContext(action, [], new object())));
        Assert.Equal(graph.Demand.Id, args["itemId"]);
    }
    [Fact] public async Task PlaybackCannotSelectARestrictedVersion()
    {
        var graph = new Graph(); graph.Denied.Add(graph.Demand.Id); graph.Http.Request.Path = "/Items/x/PlaybackInfo";
        var action = new ActionContext(graph.Http, new RouteData(), new ActionDescriptor());
        var context = new ActionExecutingContext(action, [], new Dictionary<string, object?> { ["itemId"] = graph.Local.Id, ["mediaSourceId"] = graph.Demand.Id.ToString("N") }, new object());
        var invoked = false;
        await graph.Filter.OnActionExecutionAsync(context, () => { invoked = true; return Task.FromResult(new ActionExecutedContext(action, [], new object())); });
        Assert.IsType<NotFoundResult>(context.Result); Assert.False(invoked);
    }
    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task PlaybackReportsTrackTheActualVersion(bool stopped)
    {
        var graph = new Graph();
        object report = stopped ? new PlaybackStopInfo { ItemId = graph.Local.Id, MediaSourceId = graph.Demand.Id.ToString("N") }
            : new PlaybackProgressInfo { ItemId = graph.Local.Id, MediaSourceId = graph.Demand.Id.ToString("N") };
        var action = new ActionContext(graph.Http, new RouteData(), new ActionDescriptor());
        var context = new ActionExecutingContext(action, [], new Dictionary<string, object?> { ["report"] = report }, new object());
        await graph.Filter.OnActionExecutionAsync(context, () => Task.FromResult(new ActionExecutedContext(action, [], new object())));
        Assert.Equal(graph.Demand.Id, report is PlaybackStopInfo stop ? stop.ItemId : ((PlaybackProgressInfo)report).ItemId);
    }
    [Fact] public async Task PlaybackResponseRetainsNegotiatedCodecAndStreamData()
    {
        var graph = new Graph();
        var source = new MediaSourceInfo { Id = graph.Demand.Id.ToString("N"), Name = "1080p", TranscodingUrl = "/negotiated/url" };
        var result = new ObjectResult(new PlaybackInfoResponse { MediaSources = [source] });
        var action = new ActionContext(graph.Http, new RouteData(), new ActionDescriptor());
        await graph.Filter.OnResultExecutionAsync(new(action, [], result, new object()), () => Task.FromResult(new ResultExecutedContext(action, [], result, new object())));
        Assert.Equal("/negotiated/url", source.TranscodingUrl); Assert.StartsWith("On Demand · ", source.Name);
    }
    [Fact] public void AdministrationEndpointsRequireElevatedAuthentication()
    {
        var policy = Assert.Single(typeof(BridgeController).GetCustomAttributes<AuthorizeAttribute>());
        Assert.Equal("RequiresElevation", policy.Policy);
    }

    private sealed class Graph
    {
        public User User { get; } = new("test", "auth", "reset");
        public FakeMovie Local { get; } = new() { Id = Guid.NewGuid(), Path = "/local/a.mkv" };
        public FakeMovie Demand { get; } = new() { Id = Guid.NewGuid(), Path = "/demand/a.strm" };
        public HashSet<Guid> Denied { get; } = [];
        public bool Authenticated { get; set; } = true;
        public BridgeIndex Index { get; } = new();
        public ILibraryManager Library { get; }
        public PlaybackSources Sources { get; }
        public RequestUser RequestUser { get; }
        public DefaultHttpContext Http { get; } = new();
        public BridgeApiFilter Filter { get; }
        public Graph(bool fingerprint = false)
        {
            Local.Name = Demand.Name = "Example";
            Local.ProviderIds["Tmdb"] = Demand.ProviderIds["Tmdb"] = "1";
            string? Signature(FakeMovie movie) => fingerprint ? PlanBuilder.Signature(new(movie.Id, "Movie", movie.Name, movie.Path,
                Guid.Empty, "", 0, movie.ProviderIds)) : null;
            Index.Publish(new([
                new(Local.Id, Local.Path, "Movie", "group", null, "Local", 0, Signature(Local)),
                new(Demand.Id, Demand.Path, "Movie", "group", null, "On Demand", 1, Signature(Demand))],
                [new("group", "Movie", "Example", [Local.Id, Demand.Id])], []));
            Library = StubProxy.Create<ILibraryManager>((method, args) => method.Name == "GetItemById" && args?[0] is Guid id
                && !Denied.Contains(id) ? id == Local.Id ? Local : id == Demand.Id ? Demand : null : null);
            Sources = new(Library, Index, () => true);
            var authorization = StubProxy.Create<IAuthorizationContext>((_, _) => Task.FromResult(new AuthorizationInfo { IsAuthenticated = Authenticated, User = User }));
            RequestUser = new(new HttpContextAccessor { HttpContext = Http }, authorization, StubProxy.Create<IUserManager>((_, _) => User));
            Filter = new(Sources, RequestUser, Library, Index);
        }
    }
    private sealed class FakeMovie : Movie
    {
        public override IReadOnlyList<MediaSourceInfo> GetMediaSources(bool enablePathSubstitution) =>
            [new() { Id = Id.ToString("N"), Path = Path, Name = "1080p" }];
    }
}

public class StubProxy : DispatchProxy
{
    public Func<MethodInfo, object?[]?, object?> Handler { get; set; } = (_, _) => null;
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args);
    public static T Create<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
    {
        var proxy = Create<T, StubProxy>(); ((StubProxy)(object)proxy).Handler = handler; return proxy;
    }
}
