using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.LibraryBridge.Core;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Querying;
using Xunit;

namespace Jellyfin.Plugin.LibraryBridge.Tests;

public sealed class ResumeTests
{
    private static Movie Movie(string name, string key) => new()
        { Id = Guid.NewGuid(), Name = name, Path = "/media/" + Guid.NewGuid(), PresentationUniqueKey = key };
    private sealed class Graph
    {
        public Movie A { get; } = Movie("A local", "a");
        public Movie Alternate { get; } = Movie("A on demand", "a");
        public Movie B { get; } = Movie("B", "b");
        public Movie C { get; } = Movie("C", "c");
        public BridgeIndex Index { get; } = new();
        public ResumeRepository Repository { get; }
        public InternalItemsQuery? Received { get; private set; }
        public BaseItem[] Candidates { get; set; }
        public int PassthroughCalls { get; private set; }
        public Graph(bool enabled = true)
        {
            Candidates = [A, Alternate, B, C];
            Index.Publish(new([new(A.Id, A.Path, "Movie", "a", null, "Local", 0),
                    new(Alternate.Id, Alternate.Path, "Movie", "a", null, "On Demand", 1)],
                [new("a", "Movie", "A", [A.Id, Alternate.Id])], []));
            var inner = StubProxy.Create<IItemRepository>((method, args) =>
            {
                Received = (InternalItemsQuery)args![0]!;
                if (method.Name == "GetItemList") return Candidates.Where(i => !Received.ExcludeItemIds.Contains(i.Id)).ToArray();
                PassthroughCalls++;
                if (method.Name == "GetItems") return new QueryResult<BaseItem>(Candidates);
                if (method.Name == "GetItemIdsList") return Candidates.Select(i => i.Id).ToArray();
                return null;
            });
            Repository = new(inner, Index, () => enabled);
        }
        public InternalItemsQuery Query() => new(new User("resume", "auth", "reset"))
            { IsResumable = true, StartIndex = 0, Limit = 2, EnableTotalRecordCount = true };
    }
    [Fact] public void DuplicateResumesAreCollapsedBeforeThePageIsFilled()
    {
        var graph = new Graph(); var result = graph.Repository.GetItems(graph.Query());
        Assert.Equal(new[] { graph.A.Id, graph.B.Id }, result.Items.Select(i => i.Id));
        Assert.Equal(3, result.TotalRecordCount);
    }
    [Fact] public void LaterPagesUseLogicalOffsetsAndCorrectTotals()
    {
        var graph = new Graph(); var query = graph.Query(); query.StartIndex = 1; query.Limit = 1;
        var result = graph.Repository.GetItems(query);
        Assert.Equal(graph.B.Id, Assert.Single(result.Items).Id); Assert.Equal(3, result.TotalRecordCount); Assert.Equal(1, result.StartIndex);
    }
    [Fact] public void NativeOrderSelectsASurvivorWithoutRewritingItsResumeData()
    {
        var graph = new Graph(); graph.Candidates = [graph.Alternate, graph.A, graph.B];
        var result = graph.Repository.GetItems(graph.Query());
        Assert.Same(graph.Alternate, result.Items[0]);
    }
    [Fact] public void ACopyWithoutProgressIsNotAddedToTheResumeList()
    {
        var graph = new Graph(); graph.Candidates = [graph.Alternate];
        Assert.Equal(graph.Alternate.Id, Assert.Single(graph.Repository.GetItems(graph.Query()).Items).Id);
    }
    [Fact] public void UserLibraryAndParentalFiltersArePreservedWithoutChangingTheOriginalQuery()
    {
        var graph = new Graph(); var query = graph.Query(); query.StartIndex = 5; query.Limit = 10;
        query.TopParentIds = [Guid.NewGuid()]; query.Tags = ["allowed"]; query.ExcludeTags = ["blocked"];
        graph.Repository.GetItems(query);
        Assert.NotSame(query, graph.Received); Assert.Same(query.User, graph.Received!.User);
        Assert.Equal(query.TopParentIds, graph.Received.TopParentIds); Assert.Equal(query.Tags, graph.Received.Tags);
        Assert.Equal(query.ExcludeTags, graph.Received.ExcludeTags); Assert.True(graph.Received.IsResumable);
        Assert.Null(graph.Received.Limit); Assert.Equal(0, graph.Received.StartIndex);
        Assert.Equal(5, query.StartIndex); Assert.Equal(10, query.Limit); Assert.True(query.EnableTotalRecordCount);
    }
    [Fact] public void ActiveSessionExclusionCoversEveryCopyOfTheItem()
    {
        var graph = new Graph(); var query = graph.Query(); query.ExcludeItemIds = [graph.A.Id];
        var result = graph.Repository.GetItems(query);
        Assert.Equal(new[] { graph.B.Id, graph.C.Id }, result.Items.Select(i => i.Id));
        Assert.Contains(graph.Alternate.Id, graph.Received!.ExcludeItemIds);
        Assert.Equal(new[] { graph.A.Id }, query.ExcludeItemIds);
    }
    [Fact] public void ItemsOutsideTheBridgeAreNotCollapsedJustBecauseTheirKeysMatch()
    {
        var graph = new Graph(); graph.B.PresentationUniqueKey = graph.C.PresentationUniqueKey = "a";
        var query = graph.Query(); query.Limit = null;
        Assert.Equal(3, graph.Repository.GetItems(query).Items.Count);
    }
    [Fact] public void ChangedItemsAreNotHiddenByAStaleMatch()
    {
        var graph = new Graph(); graph.Alternate.Path = "/reidentified.mkv";
        Assert.Equal(4, graph.Repository.GetItems(graph.Query()).TotalRecordCount);
    }
    [Fact] public void ItemIdListCallersGetTheSameDeduplicatedPage()
    {
        var graph = new Graph();
        Assert.Equal(new[] { graph.A.Id, graph.B.Id }, graph.Repository.GetItemIdsList(graph.Query()));
    }
    [Fact] public void NoCountRequestsKeepNativePageCountSemantics()
    {
        var graph = new Graph(); var query = graph.Query(); query.EnableTotalRecordCount = false;
        Assert.Equal(2, graph.Repository.GetItems(query).TotalRecordCount);
    }
    [Fact] public void DisabledPluginDelegatesTheOriginalQuery()
    {
        var graph = new Graph(false); var query = graph.Query();
        Assert.Equal(4, graph.Repository.GetItems(query).Items.Count);
        Assert.Same(query, graph.Received); Assert.Equal(1, graph.PassthroughCalls);
    }
    [Theory] [InlineData(false)] [InlineData(null)]
    public void OrdinaryQueriesAreNotIntercepted(bool? resumable)
    {
        var graph = new Graph(); var query = graph.Query(); query.IsResumable = resumable;
        graph.Repository.GetItems(query);
        Assert.Same(query, graph.Received); Assert.Equal(1, graph.PassthroughCalls);
    }
}
