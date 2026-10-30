using Jellyfin.Plugin.ParentalGuide.Core;
using Jellyfin.Plugin.ParentalGuide.Providers;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ParentalGuide;

public sealed class ParentalGuideTask(ILibraryManager library, ILogger<ParentalGuideTask> logger) : IScheduledTask
{
    private static readonly SemaphoreSlim RunGate = new(1, 1);

    public string Name => "Parental Guide Metadata Sync";
    public string Key => "ParentalGuideMetadataSync";
    public string Description => "Synchronize parental certifications and language/Parents Guide tags. Defaults to dry run; configure in Plugins > Parental Guide.";
    public string Category => "Library";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = TimeSpan.FromHours(3).Ticks
        };
    }

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        await RunGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var plugin = Plugin.Instance ?? throw new InvalidOperationException("Parental Guide plugin is not initialized.");
            var options = plugin.Configuration.Snapshot();
            options.Validate();
            var state = new CompletionStore(Path.Combine(plugin.DataFolderPath, "completed-v1.jsonl"));
            // Check state before consuming the one-shot setting; a corrupt file must not trigger a fresh scan.
            await state.LoadAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (options.ForceFullScanOnNextRun)
            {
                plugin.Configuration.ForceFullScanOnNextRun = false;
                plugin.SaveConfiguration();
            }

            // Own the client for this run: factory request logging can expose OMDb's query-string API key.
            using var http = new HttpClient(new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5)
            }) { Timeout = Timeout.InfiniteTimeSpan };
            var provider = new MetadataSource(new RetryingJsonClient(http, options, logger), options);
            var runner = new SyncRunner(new NativeMediaLibrary(library), provider, state, logger);
            await runner.RunAsync(options, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation("Parental Guide cancelled. Completed live items are saved; unfinished items remain pending.");
            throw;
        }
        catch (Exception error)
        {
            logger.LogError("Parental Guide stopped: {Reason}", error.Message);
            throw;
        }
        finally
        {
            RunGate.Release();
        }
    }
}
