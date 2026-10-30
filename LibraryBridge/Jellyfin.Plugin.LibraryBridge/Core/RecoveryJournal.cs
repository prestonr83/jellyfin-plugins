using System.Text.Json;

namespace Jellyfin.Plugin.LibraryBridge.Core;

public sealed record KeyState(Guid Id, string Path, string Kind, string? PresentationKey, string? SeriesKey);
public sealed record RecoveryEntry(KeyState Original, string? AppliedPresentationKey, string? AppliedSeriesKey);
public sealed record RecoveryDocument(int SchemaVersion, RecoveryEntry[] Entries);

/// <summary>Write-ahead journal: original values reach disk before Jellyfin is allowed to save a changed key.</summary>
public sealed class RecoveryJournal(string path)
{
    private readonly object _gate = new();
    private Dictionary<Guid, RecoveryEntry>? _entries;
    public RecoveryEntry? Find(Guid id)
    {
        lock (_gate) { EnsureLoaded(); return _entries!.GetValueOrDefault(id); }
    }
    public IReadOnlyList<RecoveryEntry> Read()
    {
        lock (_gate) { EnsureLoaded(); return _entries!.Values.ToArray(); }
    }
    public void Capture(IEnumerable<(KeyState Original, KeyAssignment Applied)> changes)
    {
        lock (_gate)
        {
            EnsureLoaded();
            var updates = new Dictionary<Guid, RecoveryEntry>();
            foreach (var (original, applied) in changes)
            {
                var baseline = original;
                if (!_entries!.TryGetValue(original.Id, out var previous)) updates.TryGetValue(original.Id, out previous);
                if (previous is not null)
                {
                    if (previous.Original.Path != original.Path || previous.Original.Kind != original.Kind)
                        throw new InvalidDataException($"Item identity changed; recovery is required for {original.Id}.");
                    baseline = previous.Original;
                }
                else if (baseline.PresentationKey?.StartsWith("librarybridge:", StringComparison.Ordinal) == true
                         || baseline.SeriesKey?.StartsWith("librarybridge:", StringComparison.Ordinal) == true)
                    throw new InvalidDataException($"Original keys are missing for {original.Id}; retain the recovery journal and restore before continuing.");
                var entry = new RecoveryEntry(baseline, applied.PresentationKey ?? original.PresentationKey, applied.SeriesKey ?? original.SeriesKey);
                if (entry == previous) continue;
                updates[original.Id] = entry;
            }
            if (updates.Count == 0) return;
            var next = new Dictionary<Guid, RecoveryEntry>(_entries!);
            foreach (var entry in updates) next[entry.Key] = entry.Value;
            Save(next);
        }
    }
    public void Remove(IEnumerable<Guid> ids)
    {
        lock (_gate)
        {
            EnsureLoaded();
            var next = new Dictionary<Guid, RecoveryEntry>(_entries!);
            foreach (var id in ids) next.Remove(id);
            Save(next);
        }
    }
    private void EnsureLoaded()
    {
        if (_entries is not null) return;
        if (!File.Exists(path)) { _entries = []; return; }
        RecoveryDocument document;
        try { document = JsonSerializer.Deserialize<RecoveryDocument>(File.ReadAllText(path))
            ?? throw new InvalidDataException("Library Bridge recovery journal is empty or corrupt."); }
        catch (JsonException error) { throw new InvalidDataException("Library Bridge recovery journal is corrupt; no new changes were saved.", error); }
        if (document.SchemaVersion != 1 || document.Entries is null)
            throw new InvalidDataException("Unsupported Library Bridge recovery journal schema.");
        if (document.Entries.Any(e => e?.Original is null || e.Original.Id == Guid.Empty || string.IsNullOrEmpty(e.Original.Path))
            || document.Entries.Select(e => e.Original.Id).Distinct().Count() != document.Entries.Length)
            throw new InvalidDataException("Invalid Library Bridge recovery journal entries.");
        _entries = document.Entries.ToDictionary(e => e.Original.Id);
    }
    private void Save(Dictionary<Guid, RecoveryEntry> entries)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        var temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None,
                   4096, FileOptions.WriteThrough))
        {
            JsonSerializer.Serialize(stream, new RecoveryDocument(1, entries.Values.ToArray()));
            stream.Flush(true);
        }
        File.Move(temporary, path, true);
        _entries = entries;
    }
}
