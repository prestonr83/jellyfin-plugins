using System.Text;
using System.Text.Json;

namespace Jellyfin.Plugin.ParentalGuide.Core;

public sealed record Completion(int Version, Guid Id, string Identity, string? InferredRating, DateTimeOffset CompletedAt, bool Completed);

/// <summary>An append-only, flushed checkpoint per completed item. Errors never become checkpoints.</summary>
public sealed class CompletionStore(string path)
{
    private readonly Dictionary<Guid, Completion> _completed = [];

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        _completed.Clear();
        if (!File.Exists(path))
        {
            return;
        }

        // Our writer always terminates records. A torn final append must not be joined to the next record.
        await using (var tail = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (tail.Length > 0)
            {
                tail.Seek(-1, SeekOrigin.End);
                if (tail.ReadByte() != '\n')
                {
                    throw new InvalidDataException("Parental Guide completion state has an incomplete final record. Restore the state file or move it aside deliberately to start a fresh scan.");
                }
            }
        }

        using var reader = new StreamReader(path, Encoding.UTF8);
        var lineNumber = 0;
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            lineNumber++;
            try
            {
                var entry = JsonSerializer.Deserialize<Completion>(line);
                if (entry is null || entry.Version != 1 || entry.Id == Guid.Empty || string.IsNullOrWhiteSpace(entry.Identity))
                {
                    throw new JsonException("Invalid checkpoint.");
                }

                _completed[entry.Id] = entry;
            }
            catch (JsonException)
            {
                // Fail closed instead of unexpectedly rewriting an entire library after state corruption.
                throw new InvalidDataException($"Invalid Parental Guide completion state at line {lineNumber}. Restore the state file or move it aside deliberately to start a fresh scan.");
            }
        }
    }

    public bool IsCompleted(MediaTitle item) => _completed.TryGetValue(item.Id, out var entry) && entry.Identity == item.Identity && entry.Completed;

    public string? LastInferredRating(MediaTitle item) =>
        _completed.TryGetValue(item.Id, out var entry) && entry.Identity == item.Identity ? entry.InferredRating : null;

    public Task MarkPendingAsync(MediaTitle item) => AppendAsync(new Completion(1, item.Id, item.Identity,
        LastInferredRating(item), DateTimeOffset.UtcNow, false));

    public Task RecordAsync(MediaTitle item, MetadataPlan plan)
    {
        var entry = new Completion(1, item.Id, item.Identity,
            plan.Inferred ? plan.Rating :
            string.Equals(LastInferredRating(item), plan.Rating, StringComparison.OrdinalIgnoreCase) ? plan.Rating : null,
            DateTimeOffset.UtcNow, true);
        return AppendAsync(entry);
    }

    private async Task AppendAsync(Completion entry)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        // Once a metadata commit succeeds, finish its checkpoint even if cancellation just arrived.
        await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry) + "\n");
        await stream.WriteAsync(bytes, CancellationToken.None).ConfigureAwait(false);
        await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
        _completed[entry.Id] = entry;
    }
}
