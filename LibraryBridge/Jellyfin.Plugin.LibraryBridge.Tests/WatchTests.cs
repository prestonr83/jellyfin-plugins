using Jellyfin.Plugin.LibraryBridge.Core;
using Xunit;

namespace Jellyfin.Plugin.LibraryBridge.Tests;

public sealed class WatchTests
{
    [Fact] public void ExistingCompletedWatchWinsOverAnUnstartedCopy()
    {
        var state = WatchState.Reconcile([new(true, 0, 1, DateTime.UtcNow.AddDays(-1)), new(false, 0, 0, null)]);
        Assert.True(state.Played); Assert.Equal(0, state.PositionTicks); Assert.Equal(1, state.PlayCount);
    }
    [Fact] public void ResumeUsesLatestPlayedCopy()
    {
        var now = DateTime.UtcNow;
        var state = WatchState.Reconcile([new(false, 200, 1, now.AddDays(-1)), new(false, 100, 2, now)]);
        Assert.Equal(100, state.PositionTicks); Assert.Equal(2, state.PlayCount);
    }
    [Fact] public void ResumeScalesForDifferentRuntime() => Assert.Equal(200, new WatchState(false, 100, 1, null).PositionFor(1000, 2000));
    [Fact] public void UnknownStrmRuntimeUsesReportedPosition() => Assert.Equal(100, new WatchState(false, 100, 1, null).PositionFor(null, null));
    [Fact] public void ResumeCannotExceedDestinationRuntime() => Assert.Equal(49, new WatchState(false, 100, 1, null).PositionFor(null, 50));
    [Fact] public void CompletedItemsHaveNoResumePosition() => Assert.Equal(0, new WatchState(true, 100, 1, null).PositionFor(1000, 2000));
}
