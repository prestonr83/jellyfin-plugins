using Jellyfin.Plugin.ParentalGuide.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ParentalGuide.Tests;

public sealed class SyncRunnerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "parental-guide-tests-" + Guid.NewGuid().ToString("N"));
    private string StatePath => Path.Combine(_directory, "completed-v1.jsonl");
    private CompletionStore Store() => new(StatePath);
    private static readonly IProgress<double> Progress = new InlineProgress(_ => { });
    private SyncRunner Runner(FakeLibrary library, FakeMetadata source, CompletionStore? state = null) =>
        new(library, source, state ?? Store(), NullLogger.Instance);

    [Fact]
    public async Task DryRunHonorsLimitAndNeverWritesOrCheckpoints()
    {
        var options = MetadataRulesTests.Options();
        options.MaximumItems = 2;
        var library = new FakeLibrary(5);
        var source = new FakeMetadata();
        var runner = Runner(library, source);
        var result = await runner.RunAsync(options, Progress, default);
        Assert.Equal(2, result.Processed);
        Assert.Equal(2, source.Lookups);
        Assert.Equal(0, library.Writes);
        Assert.False(File.Exists(StatePath));
        await runner.RunAsync(options, Progress, default);
        Assert.Equal(4, source.Lookups);
        Assert.All(library.Items.Values, item => Assert.Null(item.Rating));
    }

    [Fact]
    public async Task LiveLimitResumesAcrossRestartsAndThenOnlyNewItems()
    {
        var options = MetadataRulesTests.Options();
        options.DryRun = false;
        options.MaximumItems = 2;
        var library = new FakeLibrary(5);
        var source = new FakeMetadata();
        await Runner(library, source).RunAsync(options, Progress, default);
        await Runner(library, source).RunAsync(options, Progress, default);
        Assert.Equal(4, source.Lookups);
        await Runner(library, source).RunAsync(options, Progress, default);
        Assert.Equal(5, source.Lookups);
        Assert.Equal(0, (await Runner(library, source).RunAsync(options, Progress, default)).Processed);
        var item = MetadataRulesTests.Title();
        library.Items.Add(item.Id, item);
        Assert.Equal(1, (await Runner(library, source).RunAsync(options, Progress, default)).Processed);
        Assert.All(library.Items.Values, title => Assert.Contains("Existing", title.Tags));
    }

    [Fact]
    public async Task ForcedScanHonorsLimitAndFailedForcedItemRemainsPending()
    {
        var options = MetadataRulesTests.Options();
        options.DryRun = false;
        var library = new FakeLibrary(3);
        var source = new FakeMetadata();
        await Runner(library, source).RunAsync(options, Progress, default);
        options.ForceFullScanOnNextRun = true;
        options.MaximumItems = 1;
        source.Failure = new ProviderException("temporary failure");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Runner(library, source).RunAsync(options, Progress, default));
        Assert.Equal(4, source.Lookups);
        source.Failure = null;
        options.ForceFullScanOnNextRun = false;
        Assert.Equal(1, (await Runner(library, source).RunAsync(options, Progress, default)).Processed);
    }

    [Fact]
    public async Task ErrorsStayPendingWhileOtherItemsComplete()
    {
        var options = MetadataRulesTests.Options();
        options.DryRun = false;
        var library = new FakeLibrary(3);
        var source = new FakeMetadata { FailLookupNumber = 2 };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Runner(library, source).RunAsync(options, Progress, default));
        Assert.Equal(2, library.Writes);
        Assert.Equal(1, (await Runner(library, source).RunAsync(options, Progress, default)).Processed);
        Assert.Equal(3, library.Writes);
    }

    [Fact]
    public async Task FatalFailureStopsImmediately()
    {
        var options = MetadataRulesTests.Options();
        options.DryRun = false;
        var library = new FakeLibrary(3);
        var source = new FakeMetadata { Failure = new FatalProviderException("quota exhausted") };
        var progress = new List<double>();
        await Assert.ThrowsAsync<FatalProviderException>(() => Runner(library, source).RunAsync(options,
            new InlineProgress(progress.Add), default));
        Assert.Equal(1, source.Lookups);
        Assert.Equal(0, library.Writes);
        Assert.DoesNotContain(100, progress);
    }

    [Fact]
    public async Task CancellationRetainsPriorCheckpointsAndLeavesRemainderPending()
    {
        var options = MetadataRulesTests.Options();
        options.DryRun = false;
        var library = new FakeLibrary(3);
        var source = new FakeMetadata();
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Runner(library, source).RunAsync(options,
            new InlineProgress(value => { if (value > 0) cancellation.Cancel(); }), cancellation.Token));
        Assert.Equal(1, library.Writes);
        Assert.Equal(2, (await Runner(library, source).RunAsync(options, Progress, default)).Processed);
    }

    [Fact]
    public async Task GuideTagsAreFetchedEvenWithoutOmdbMatch()
    {
        var options = MetadataRulesTests.Options();
        options.DryRun = false;
        var library = new FakeLibrary(1);
        var source = new FakeMetadata { Match = null };
        await Runner(library, source).RunAsync(options, Progress, default);
        Assert.Equal(1, source.GuideLookups);
        Assert.Equal(5, library.Items.Values.Single().Tags.Count(t => t.StartsWith("Parents Guide:")));
        Assert.Null(library.Items.Values.Single().Rating);
        Assert.Equal(0, (await Runner(library, source).RunAsync(options, Progress, default)).Processed);
    }

    [Fact]
    public async Task CurrentTagsAndRatingAreRecheckedAfterFetch()
    {
        var options = MetadataRulesTests.Options();
        options.DryRun = false;
        var library = new FakeLibrary(1);
        var source = new FakeMetadata();
        var id = library.Items.Keys.Single();
        source.OnLookup = () => library.Items[id] = library.Items[id] with { Rating = "NC-17", Tags = ["New Manual Tag"] };
        await Runner(library, source).RunAsync(options, Progress, default);
        Assert.Equal("NC-17", library.Items[id].Rating);
        Assert.Contains("New Manual Tag", library.Items[id].Tags);
    }

    [Fact]
    public async Task ChangedIdentityIsPendingAndDryFullScanDoesNotClearCompletion()
    {
        var options = MetadataRulesTests.Options();
        options.DryRun = false;
        var library = new FakeLibrary(1);
        var source = new FakeMetadata();
        await Runner(library, source).RunAsync(options, Progress, default);
        var original = await File.ReadAllTextAsync(StatePath);
        options.DryRun = true;
        options.ForceFullScanOnNextRun = true;
        await Runner(library, source).RunAsync(options, Progress, default);
        Assert.Equal(original, await File.ReadAllTextAsync(StatePath));
        options.DryRun = false;
        options.ForceFullScanOnNextRun = false;
        var id = library.Items.Keys.Single();
        library.Items[id] = library.Items[id] with { ImdbId = "tt7654321" };
        Assert.Equal(1, (await Runner(library, source).RunAsync(options, Progress, default)).Processed);
    }

    [Fact]
    public async Task CorruptStateFailsClosed()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(StatePath, "{broken\n");
        var source = new FakeMetadata();
        await Assert.ThrowsAsync<InvalidDataException>(() => Runner(new FakeLibrary(1), source).RunAsync(MetadataRulesTests.Options(), Progress, default));
        Assert.Equal(0, source.Lookups);
    }

    [Fact]
    public async Task TornFinalCheckpointFailsClosed()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(StatePath, "{\"Version\":1");
        await Assert.ThrowsAsync<InvalidDataException>(() => Store().LoadAsync(default));
    }

    [Fact]
    public async Task IdentityChangeDuringRequestNeverWritesOrCompletes()
    {
        var options = MetadataRulesTests.Options();
        options.DryRun = false;
        var library = new FakeLibrary(1);
        var id = library.Items.Keys.Single();
        var source = new FakeMetadata { OnLookup = () => library.Items[id] = library.Items[id] with { Name = "Corrected title" } };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Runner(library, source).RunAsync(options, Progress, default));
        Assert.Equal(0, library.Writes);
        Assert.False(File.Exists(StatePath));
    }

    [Fact]
    public async Task MetadataWriteFailureDoesNotCompleteItem()
    {
        var options = MetadataRulesTests.Options();
        options.DryRun = false;
        var library = new FakeLibrary(1) { FailWrites = true };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Runner(library, new FakeMetadata()).RunAsync(options, Progress, default));
        Assert.False(File.Exists(StatePath));
        library.FailWrites = false;
        Assert.Equal(1, (await Runner(library, new FakeMetadata()).RunAsync(options, Progress, default)).Processed);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    internal sealed class InlineProgress(Action<double> action) : IProgress<double>
    {
        public void Report(double value) => action(value);
    }

    internal sealed class FakeLibrary : IMediaLibrary
    {
        public Dictionary<Guid, MediaTitle> Items { get; } = [];
        public int Writes { get; private set; }
        public bool FailWrites { get; set; }
        public FakeLibrary(int count)
        {
            for (var n = 0; n < count; n++)
            {
                var item = MetadataRulesTests.Title();
                Items.Add(item.Id, item);
            }
        }
        public IReadOnlyList<Guid> GetItemIds(CancellationToken token) => Items.Keys.ToArray();
        public MediaTitle? GetItem(Guid id) => Items.GetValueOrDefault(id);
        public Task ApplyAsync(MediaTitle current, MetadataPlan plan, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (FailWrites) throw new MetadataWriteException("test save failure");
            Items[current.Id] = current with { Rating = plan.Rating, Tags = current.Tags.Concat(plan.TagsToAdd).ToArray() };
            Writes++;
            return Task.CompletedTask;
        }
    }

    internal sealed class FakeMetadata : IMetadataSource
    {
        public int Lookups { get; private set; }
        public int GuideLookups { get; private set; }
        public int? FailLookupNumber { get; init; }
        public ProviderException? Failure { get; set; }
        public Action? OnLookup { get; set; }
        public OmdbTitle? Match { get; init; } = new("tt1234567", "R", "English,French");
        public Task<OmdbTitle?> FindAsync(MediaTitle title, CancellationToken token)
        {
            Lookups++;
            if (Failure is not null) throw Failure;
            if (Lookups == FailLookupNumber) throw new ProviderException("temporary failure");
            OnLookup?.Invoke();
            return Task.FromResult(Match);
        }
        public Task<IReadOnlyList<GuideCategory>> GetGuideAsync(string imdb, CancellationToken token)
        {
            GuideLookups++;
            return Task.FromResult<IReadOnlyList<GuideCategory>>(MetadataRulesTests.Guide("Mild", 0));
        }
    }
}
