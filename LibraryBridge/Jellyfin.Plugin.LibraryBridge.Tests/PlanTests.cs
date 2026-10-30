using Jellyfin.Plugin.LibraryBridge.Core;
using Xunit;

namespace Jellyfin.Plugin.LibraryBridge.Tests;

public sealed class PlanTests
{
    private static readonly Guid Local = Guid.NewGuid(), Demand = Guid.NewGuid();
    private static CatalogItem Title(string kind, string id, Guid library, string name = "Example") =>
        new(Guid.NewGuid(), kind, name, "/" + Guid.NewGuid(), library,
            library == Local ? "Local" : "On Demand", library == Local ? 0 : 1,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Tmdb"] = id });
    private static CatalogItem Episode(CatalogItem series, int season, int episode, int? end = null) =>
        new(Guid.NewGuid(), "Episode", "Episode", "/" + Guid.NewGuid(), series.LibraryId, series.Label,
            series.Preference, new Dictionary<string, string>(), series.Id, season, episode, end);

    [Fact] public void MovieCopiesHaveOneGroupWithoutChangingTheirIdsOrPaths()
    {
        var a = Title("Movie", "123", Local); var b = Title("Movie", "123", Demand);
        var plan = PlanBuilder.Build([a, b], "pair");
        var group = Assert.Single(plan.Groups);
        Assert.Equal(new[] { a.Id, b.Id }, group.ItemIds);
        Assert.Equal(2, plan.Assignments.Length);
        Assert.Equal(a.Path, plan.Assignments.Single(x => x.Id == a.Id).Path);
        Assert.Single(plan.Assignments.Select(x => x.PresentationKey).Distinct());
    }
    [Fact] public void IdenticalNamesWithDifferentIdsDoNotMatch() =>
        Assert.Empty(PlanBuilder.Build([Title("Movie", "1", Local), Title("Movie", "2", Demand)], "pair").Groups);
    [Fact] public void NoNameOnlyFallback() => Assert.Empty(PlanBuilder.Build([
        Title("Movie", "", Local), Title("Movie", "", Demand)], "pair").Groups);
    [Fact] public void SameIdInSameLibraryDoesNotCreateACrossLibraryGroup() => Assert.Empty(PlanBuilder.Build([
        Title("Movie", "1", Local), Title("Movie", "1", Local)], "pair").Groups);
    [Fact] public void MoviesAndShowsCannotMatchEachOther() => Assert.Empty(PlanBuilder.Build([
        Title("Movie", "1", Local), Title("Series", "1", Demand)], "pair").Groups);
    [Fact] public void ConflictingSecondaryProviderIdsAreRejected()
    {
        var a = Title("Movie", "1", Local) with { ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "1", ["Imdb"] = "tt1" } };
        var b = Title("Movie", "1", Demand) with { ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "1", ["Imdb"] = "tt2" } };
        var plan = PlanBuilder.Build([a, b], "pair"); Assert.Empty(plan.Groups); Assert.Single(plan.Warnings);
    }
    [Fact] public void SharedSecondaryIdCanBridgeMissingPrimaryProvider()
    {
        var a = Title("Movie", "1", Local) with { ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "1", ["Imdb"] = "tt1" } };
        var b = Title("Movie", "", Demand) with { ProviderIds = new Dictionary<string, string> { ["Imdb"] = "tt1" } };
        Assert.Single(PlanBuilder.Build([a, b], "pair").Groups);
    }
    [Fact] public void TransitiveProviderConflictsCannotJoinRemakes()
    {
        var a = Title("Movie", "1", Local) with { ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "1", ["Imdb"] = "tt1" } };
        var b = Title("Movie", "2", Demand) with { ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "2", ["Imdb"] = "tt1" } };
        Assert.Empty(PlanBuilder.Build([a, b], "pair").Groups);
    }
    [Fact] public void SeriesEpisodesAndSeasonsShareTheNativeSeriesKey()
    {
        var a = Title("Series", "1", Local); var b = Title("Series", "1", Demand);
        var e1 = Episode(a, 1, 1); var e2 = Episode(b, 1, 1); var unique = Episode(b, 2, 4);
        var seasonA = e1 with { Id = Guid.NewGuid(), Kind = "Season", EpisodeNumber = null };
        var seasonB = e2 with { Id = Guid.NewGuid(), Kind = "Season", EpisodeNumber = null };
        var plan = PlanBuilder.Build([a, b, e1, e2, unique, seasonA, seasonB], "pair");
        var key = plan.Groups.Single(g => g.Kind == "Series").Key;
        Assert.All(plan.Assignments.Where(x => x.Kind is "Season" or "Episode"), x => Assert.Equal(key, x.SeriesKey));
        Assert.Equal(plan.Assignments.Single(x => x.Id == e1.Id).PresentationKey, plan.Assignments.Single(x => x.Id == e2.Id).PresentationKey);
        Assert.Equal(plan.Assignments.Single(x => x.Id == seasonA.Id).PresentationKey, plan.Assignments.Single(x => x.Id == seasonB.Id).PresentationKey);
        Assert.Null(plan.Assignments.Single(x => x.Id == unique.Id).PresentationKey);
    }
    [Fact] public void DifferentEpisodeNumbersNeverBecomeVersions()
    {
        var a = Title("Series", "1", Local); var b = Title("Series", "1", Demand);
        Assert.DoesNotContain(PlanBuilder.Build([a, b, Episode(a, 1, 1), Episode(b, 1, 2)], "p").Groups, g => g.Kind == "Episode");
    }
    [Fact] public void MultiEpisodeFilesDoNotMergeWithSingleEpisodes()
    {
        var a = Title("Series", "1", Local); var b = Title("Series", "1", Demand);
        Assert.DoesNotContain(PlanBuilder.Build([a, b, Episode(a, 1, 1, 2), Episode(b, 1, 1)], "p").Groups, g => g.Kind == "Episode");
    }
    [Fact] public void DifferentEpisodeOrdersAreRejected()
    {
        var a = Title("Series", "1", Local) with { DisplayOrder = "dvd" }; var b = Title("Series", "1", Demand) with { DisplayOrder = "airdate" };
        Assert.Empty(PlanBuilder.Build([a, b], "p").Groups);
    }
    [Fact] public void DefaultAndExplicitAiredOrderAreEquivalent()
    {
        var a = Title("Series", "1", Local); var b = Title("Series", "1", Demand) with { DisplayOrder = "airdate" };
        Assert.Single(PlanBuilder.Build([a, b], "p").Groups);
    }
    [Theory] [InlineData("0")] [InlineData("-1")] [InlineData("unknown")]
    public void PlaceholderProviderIdsCannotMergeUnrelatedTitles(string id) => Assert.Empty(PlanBuilder.Build([
        Title("Movie", id, Local), Title("Movie", id, Demand)], "p").Groups);
    [Fact] public void NativeVersionGroupsAreLeftAlone()
    {
        var a = Title("Movie", "1", Local) with { Eligible = false }; var b = Title("Movie", "1", Demand);
        Assert.Empty(PlanBuilder.Build([a, b], "p").Groups);
    }
    [Fact] public void StableKeysDoNotDependOnCatalogEnumerationOrder()
    {
        var a = Title("Movie", "1", Local); var b = Title("Movie", "1", Demand);
        Assert.Equal(PlanBuilder.Build([a, b], "p").Groups[0].Key, PlanBuilder.Build([b, a], "p").Groups[0].Key);
        Assert.NotEqual(PlanBuilder.Build([a, b], "p").Groups[0].Key, PlanBuilder.Build([a, b], "other-pair").Groups[0].Key);
    }
    [Theory]
    [InlineData(0)] [InlineData(1)]
    public void SpecialsAndRegularEpisodesCanHaveSelectableVersions(int season)
    {
        var a = Title("Series", "1", Local); var b = Title("Series", "1", Demand);
        Assert.Single(PlanBuilder.Build([a, b, Episode(a, season, 1), Episode(b, season, 1)], "p").Groups, g => g.Kind == "Episode");
    }
}
