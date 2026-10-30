using Jellyfin.Plugin.ParentalGuide.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ParentalGuide.Core;

public sealed record RunResult(int Processed, int Changed, int Errors);

public sealed class SyncRunner(IMediaLibrary library, IMetadataSource metadata, CompletionStore state, ILogger logger)
{
    public async Task<RunResult> RunAsync(PluginConfiguration options, IProgress<double> progress, CancellationToken cancellationToken)
    {
        options.Validate();
        progress.Report(0);
        await state.LoadAsync(cancellationToken).ConfigureAwait(false);
        var pending = new List<MediaTitle>();
        foreach (var id in library.GetItemIds(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = library.GetItem(id);
            if (item is null || (!options.ForceFullScanOnNextRun && state.IsCompleted(item)))
            {
                continue;
            }

            pending.Add(item);
            if (options.MaximumItems > 0 && pending.Count >= options.MaximumItems)
            {
                break;
            }
        }

        var processed = 0;
        var changed = 0;
        var errors = 0;
        logger.LogInformation("Parental Guide starting: DryRun={DryRun}, FullScan={FullScan}, Pending={Pending}",
            options.DryRun, options.ForceFullScanOnNextRun, pending.Count);
        foreach (var item in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!options.DryRun && options.ForceFullScanOnNextRun)
                {
                    // A failed forced retry must remain pending on the next incremental run.
                    await state.MarkPendingAsync(item).ConfigureAwait(false);
                }

                var match = await metadata.FindAsync(item, cancellationToken).ConfigureAwait(false);
                var imdbId = MetadataRules.ValidImdbId(match?.ImdbId) ? match!.ImdbId : item.ImdbId;
                IReadOnlyList<GuideCategory> guide = [];
                if (MetadataRules.ValidImdbId(imdbId) && (options.AddParentsGuideTags ||
                    (options.InferFromParentsGuide && !MetadataRules.HasCertification(match?.Rating))))
                {
                    guide = await metadata.GetGuideAsync(imdbId!, cancellationToken).ConfigureAwait(false);
                }

                cancellationToken.ThrowIfCancellationRequested();
                // Re-read after network calls to preserve edits made while the request was in flight.
                var current = library.GetItem(item.Id);
                if (current is null)
                {
                    continue;
                }

                if (current.Identity != item.Identity)
                {
                    errors++;
                    logger.LogWarning("Metadata identity changed for {Item}; leaving it pending for the next run", item.Name);
                    continue;
                }

                var plan = MetadataRules.Plan(current, match, guide, options, state.LastInferredRating(current));
                if (plan.HasChanges(current))
                {
                    logger.LogInformation("{Action} {Item} ({Id}): rating {Before} -> {After}; add tags [{Tags}]{Inferred}",
                        options.DryRun ? "Would update" : "Updating", item.Name, item.Id, current.Rating,
                        plan.Rating, string.Join(", ", plan.TagsToAdd), plan.Inferred ? " (inferred)" : "");
                    if (!options.DryRun)
                    {
                        await library.ApplyAsync(current, plan, cancellationToken).ConfigureAwait(false);
                    }

                    changed++;
                }

                if (!options.DryRun)
                {
                    // No-match/no-rating are completed outcomes too, matching the original script.
                    // Checkpoint failures must stop the task rather than silently lose incremental state.
                    await state.RecordAsync(current, plan).ConfigureAwait(false);
                }
            }
            catch (FatalProviderException)
            {
                throw;
            }
            catch (Exception error) when (error is ProviderException or MetadataWriteException)
            {
                errors++;
                logger.LogWarning("Failed {Item} ({Id}): {Reason}. It remains pending.", item.Name, item.Id, error.Message);
            }
            finally
            {
                processed++;
                // Leave 100% for normal completion; cancellation/fatal errors must not look successful.
                progress.Report(Math.Min(99, processed * 100.0 / pending.Count));
            }
        }

        logger.LogInformation("Parental Guide finished: DryRun={DryRun}, Processed={Processed}, Changed={Changed}, Errors={Errors}",
            options.DryRun, processed, changed, errors);
        if (errors > 0)
        {
            throw new InvalidOperationException($"Parental Guide encountered {errors} item error(s); see the server log. Failed items will be retried next run.");
        }

        progress.Report(100);
        return new RunResult(processed, changed, errors);
    }
}

public sealed class MetadataWriteException(string message) : Exception(message);
