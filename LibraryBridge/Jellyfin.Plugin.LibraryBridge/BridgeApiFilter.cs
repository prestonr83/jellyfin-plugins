using Jellyfin.Plugin.LibraryBridge.Core;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Session;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jellyfin.Plugin.LibraryBridge;

/// <summary>Advertises versions in standard item DTOs; no web-only script or custom player is required.</summary>
public sealed class BridgeApiFilter(PlaybackSources sources, RequestUser requestUser, ILibraryManager library,
    BridgeIndex index) : IAsyncResultFilter, IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (sources.Enabled && context.HttpContext.Request.Path.Value?.EndsWith("/Episodes", StringComparison.OrdinalIgnoreCase) == true
            && context.ActionArguments.TryGetValue("seriesId", out var queueSeries) && queueSeries is Guid seriesId
            && context.ActionArguments.TryGetValue("startItemId", out var queueStart) && queueStart is Guid startId)
        {
            var user = await requestUser.ResolveAsync().ConfigureAwait(false);
            if (user is not null) context.ActionArguments["startItemId"] = sources.EpisodeQueueStart(startId, seriesId, user);
        }
        if (sources.Enabled)
        {
            foreach (var report in context.ActionArguments.Values)
            {
                var (reportedId, mediaSourceId) = report switch
                {
                    PlaybackProgressInfo progress => (progress.ItemId, progress.MediaSourceId),
                    PlaybackStopInfo stop => (stop.ItemId, stop.MediaSourceId),
                    _ => (Guid.Empty, null)
                };
                if (!Guid.TryParse(mediaSourceId, out var selectedId) || selectedId == reportedId
                    || !index.Current.Groups.TryGetValue(reportedId, out var versions) || !versions.ItemIds.Contains(selectedId)) continue;
                var reportUser = await requestUser.ResolveAsync().ConfigureAwait(false);
                if (reportUser is null || !sources.CanSelect(reportedId, selectedId, reportUser))
                { context.Result = new NotFoundResult(); return; }
                // Track the actual source's duration and per-version audio/subtitle preferences.
                if (report is PlaybackProgressInfo progressReport) progressReport.ItemId = selectedId;
                if (report is PlaybackStopInfo stopReport) stopReport.ItemId = selectedId;
            }
        }
        if (sources.Enabled && context.HttpContext.Request.Path.Value?.EndsWith("/PlaybackInfo", StringComparison.OrdinalIgnoreCase) == true
            && context.ActionArguments.TryGetValue("itemId", out var value) && value is Guid itemId
            && index.Current.Groups.TryGetValue(itemId, out var group))
        {
            context.ActionArguments.TryGetValue("mediaSourceId", out var selected);
            if (selected is null && context.ActionArguments.TryGetValue("playbackInfoDto", out var dto) && dto is not null)
                selected = dto.GetType().GetProperty("MediaSourceId")?.GetValue(dto);
            if (Guid.TryParse(selected?.ToString(), out var sourceId) && sourceId != itemId && group.ItemIds.Contains(sourceId))
            {
                var user = await requestUser.ResolveAsync().ConfigureAwait(false);
                if (user is not null && sources.CanSelect(itemId, sourceId, user))
                    // Let Jellyfin probe the selected STRM, and construct its own transcode/direct URLs and permissions.
                    context.ActionArguments["itemId"] = sourceId;
                else { context.Result = new NotFoundResult(); return; }
            }
        }
        await next().ConfigureAwait(false);
    }
    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        if (sources.Enabled && context.Result is ObjectResult result)
        {
            var user = await requestUser.ResolveAsync().ConfigureAwait(false);
            if (user is not null)
            {
                switch (result.Value)
                {
                    case BaseItemDto item: Enrich(item); break;
                    case QueryResult<BaseItemDto> query: foreach (var item in query.Items) Enrich(item); break;
                    case IEnumerable<BaseItemDto> items: foreach (var item in items) Enrich(item); break;
                    case PlaybackInfoResponse playback: sources.Label(playback.MediaSources); break;
                }
                void Enrich(BaseItemDto dto)
                {
                    if (!index.Current.Groups.TryGetValue(dto.Id, out var group) || group.Kind == "Series") return;
                    var item = library.GetItemById<BaseItem>(dto.Id, user);
                    if (item is null) return;
                    var versions = sources.Get(item, user, false);
                    if (versions.Count < 2) return;
                    dto.MediaSourceCount = versions.Count;
                    // Most list endpoints don't request MediaSources. Keep them light; detail responses get the chooser.
                    if (dto.MediaSources is not null) dto.MediaSources = versions.ToArray();
                }
            }
        }
        await next().ConfigureAwait(false);
    }
}
