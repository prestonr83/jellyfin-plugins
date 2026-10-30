using Jellyfin.Plugin.ParentalGuide.Configuration;
using Jellyfin.Plugin.ParentalGuide.Core;
using Microsoft.Extensions.Logging.Abstractions;
using MediaBrowser.Model.Tasks;
using Xunit;

namespace Jellyfin.Plugin.ParentalGuide.Tests;

public class MetadataRulesTests
{
    internal static MediaTitle Title(string? rating = null) => new(Guid.NewGuid(), "Example", 2024, false, "tt1234567", rating, ["Existing", "lang:English"]);
    internal static PluginConfiguration Options() => new() { OmdbApiKey = "test-key", ImdbDelaySeconds = 0 };
    internal static GuideCategory[] Guide(string severity, int votes = 3) =>
        MetadataRules.CategoryNames.Select(name => new GuideCategory(name, severity, votes)).ToArray();

    [Fact]
    public void SafeDefaultsAndNativeTask()
    {
        var settings = new PluginConfiguration();
        Assert.True(settings.DryRun);
        Assert.True(settings.OnlyUpgradeRatings);
        Assert.True(settings.AddLanguageTags);
        Assert.True(settings.AddParentsGuideTags);
        Assert.False(settings.InferFromParentsGuide);
        var task = new ParentalGuideTask(null!, NullLogger<ParentalGuideTask>.Instance);
        Assert.Equal("Parental Guide Metadata Sync", task.Name);
        var trigger = Assert.Single(task.GetDefaultTriggers());
        Assert.Equal(TaskTriggerInfoType.DailyTrigger, trigger.Type);
        Assert.Equal(TimeSpan.FromHours(3).Ticks, trigger.TimeOfDayTicks);
        Assert.Contains("Jellyfin.Plugin.ParentalGuide.Configuration.configPage.html", typeof(Plugin).Assembly.GetManifestResourceNames());
    }

    [Fact]
    public void AllFiveTagsWithoutCertificationOrVotes()
    {
        var plan = MetadataRules.Plan(Title(), null, Guide("Severe", 0), Options(), null);
        Assert.Null(plan.Rating);
        Assert.Equal(5, plan.TagsToAdd.Count);
        Assert.All(MetadataRules.CategoryNames, name => Assert.Contains($"Parents Guide: {name} - Severe", plan.TagsToAdd));
    }

    [Fact]
    public void AddsLanguagesAndPreservesExistingTags()
    {
        var item = Title("PG");
        var match = new OmdbTitle(item.ImdbId, "R", " English, French, french,  N/A, Unknown,  Spanish   ");
        var plan = MetadataRules.Plan(item, match, [], Options(), null);
        Assert.Equal("R", plan.Rating);
        Assert.Equal(new[] { "Lang:French", "Lang:Spanish" }, plan.TagsToAdd);
        Assert.Equal(new[] { "Existing", "lang:English" }, item.Tags);
    }

    [Theory]
    [InlineData("R", "PG", "R")]
    [InlineData("TV-MA", "PG-13", "TV-MA")]
    [InlineData("PG", "R", "R")]
    [InlineData("US:PG_13", "R", "R")]
    [InlineData("GB:15", "R", "GB:15")]
    [InlineData("PG", "GB:18", "PG")]
    [InlineData("TV-14", "PG-13", "TV-14")]
    [InlineData("NR", "PG", "PG")]
    public void OnlyStricterPreservesUnknownAndEqualRatings(string current, string candidate, string expected)
    {
        Assert.Equal(expected, MetadataRules.Plan(Title(current), new OmdbTitle(null, candidate, null), [], Options(), null).Rating);
    }

    [Fact]
    public void CanAllowRatingDowngrade()
    {
        var options = Options();
        options.OnlyUpgradeRatings = false;
        Assert.Equal("PG", MetadataRules.Plan(Title("R"), new OmdbTitle(null, "PG", null), [], options, null).Rating);
    }

    [Theory]
    [InlineData("N/A")]
    [InlineData("Unrated")]
    [InlineData("Not Rated")]
    [InlineData("NR")]
    [InlineData(null)]
    public void MissingExternalCertificationPreservesCurrent(string? rating)
    {
        Assert.Equal("R", MetadataRules.Plan(Title("R"), new OmdbTitle(null, rating, null), [], Options(), null).Rating);
    }

    [Theory]
    [InlineData("None", false, "G")]
    [InlineData("Mild", false, "PG")]
    [InlineData("Moderate", false, "PG-13")]
    [InlineData("Severe", false, "R")]
    [InlineData("None", true, "TV-G")]
    [InlineData("Mild", true, "TV-PG")]
    [InlineData("Moderate", true, "TV-14")]
    [InlineData("Severe", true, "TV-MA")]
    public void InferenceMapping(string severity, bool series, string expected) =>
        Assert.Equal(expected, MetadataRules.Infer(Guide(severity), series, 3));

    [Fact]
    public void LowerInferenceRequiresAllCategoriesAndEnoughVotes()
    {
        Assert.Null(MetadataRules.Infer(Guide("Mild")[..4], false, 3));
        var guide = Guide("Mild");
        guide[0] = guide[0] with { Severity = "Severe", Votes = 2 };
        Assert.Null(MetadataRules.Infer(guide, false, 3));
        Assert.Equal("R", MetadataRules.Infer([guide[0] with { Votes = 3 }], false, 3));
    }

    [Fact]
    public void InferenceIsOptInAndDoesNotOverrideCertification()
    {
        var options = Options();
        Assert.Null(MetadataRules.Plan(Title(), null, Guide("Severe"), options, null).Rating);
        options.InferFromParentsGuide = true;
        var inferred = MetadataRules.Plan(Title(), null, Guide("Severe"), options, null);
        Assert.Equal("R", inferred.Rating);
        Assert.True(inferred.Inferred);
        Assert.Contains("Rating Source: IMDb Parents Guide", inferred.TagsToAdd);
        Assert.Equal("PG", MetadataRules.Plan(Title(), new OmdbTitle(null, "PG", null), Guide("Severe"), options, null).Rating);
    }

    [Fact]
    public void ManuallyChangedInferredRatingIsProtectedWhenUpgradeModeOff()
    {
        var options = Options();
        options.InferFromParentsGuide = true;
        options.OnlyUpgradeRatings = false;
        Assert.Equal("PG-13", MetadataRules.Plan(Title("PG-13"), null, Guide("Severe"), options, "PG").Rating);
        Assert.Equal("R", MetadataRules.Plan(Title("PG"), null, Guide("Severe"), options, "PG").Rating);
    }

    [Fact]
    public void SettingsValidationAndSnapshot()
    {
        var options = Options();
        var frozen = options.Snapshot();
        options.DryRun = false;
        Assert.True(frozen.DryRun);
        options.MaximumItems = -1;
        Assert.Throws<InvalidOperationException>(options.Validate);
        options.MaximumItems = 0;
        options.RetryBaseDelaySeconds = double.NaN;
        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void UnicodeTitlesRemainDistinctDuringFallbackNormalization()
    {
        Assert.NotEqual(MetadataRules.NormalizeTitle("東京物語"), MetadataRules.NormalizeTitle("七人の侍"));
        Assert.Equal("amelie", MetadataRules.NormalizeTitle("Amelie!"));
    }
}
