using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Jellyfin.Plugin.LibraryBridge.Core;

public sealed record CatalogItem(Guid Id, string Kind, string Name, string Path, Guid LibraryId,
    string Label, int Preference, IReadOnlyDictionary<string, string> ProviderIds,
    Guid SeriesId = default, int? SeasonNumber = null, int? EpisodeNumber = null,
    int? EpisodeEnd = null, string DisplayOrder = "", bool Eligible = true);

public sealed record KeyAssignment(Guid Id, string Path, string Kind, string? PresentationKey,
    string? SeriesKey, string Label, int Preference, string? Signature = null);

public sealed record BridgeGroup(string Key, string Kind, string Name, Guid[] ItemIds);

public sealed record GroupingPlan(KeyAssignment[] Assignments, BridgeGroup[] Groups, string[] Warnings)
{
    public static readonly GroupingPlan Empty = new([], [], []);
}

/// <summary>Only explicit provider identities bridge titles. Names are display text, never match keys.</summary>
public static class PlanBuilder
{
    private static readonly string[] Providers = ["Tmdb", "Tvdb", "Imdb"];

    public static GroupingPlan Build(IReadOnlyList<CatalogItem> items, string pairIdentity)
    {
        var assignments = new Dictionary<Guid, KeyAssignment>();
        var groups = new List<BridgeGroup>();
        var warnings = new List<string>();
        var titles = items.Where(i => i.Kind is "Movie" or "Series" && i.Eligible).ToArray();
        var remaining = titles.Select(i => i.Id).ToHashSet();
        var byToken = titles.SelectMany(i => Tokens(i).Select(t => (Token: t, Item: i)))
            .GroupBy(x => x.Token).ToDictionary(g => g.Key, g => g.Select(x => x.Item).ToArray());
        foreach (var title in titles)
        {
            if (!remaining.Remove(title.Id)) continue;
            var component = new List<CatalogItem>();
            var queue = new Queue<CatalogItem>();
            queue.Enqueue(title);
            while (queue.TryDequeue(out var current))
            {
                component.Add(current);
                foreach (var token in Tokens(current))
                foreach (var neighbor in byToken[token])
                    if (remaining.Remove(neighbor.Id)) queue.Enqueue(neighbor);
            }
            if (component.Select(i => i.LibraryId).Distinct().Count() < 2) continue;
            if (HasConflicts(component) || component.Select(i => NormalizeOrder(i.DisplayOrder)).Distinct().Count() > 1)
            {
                warnings.Add($"Skipped conflicting provider IDs or episode order: {title.Name} ({title.Id}).");
                continue;
            }
            var identity = component.SelectMany(Tokens).Order(StringComparer.Ordinal).First();
            var key = Key(pairIdentity + ":" + identity);
            AddGroup(component, key, title.Kind, title.Name, null);
            if (title.Kind != "Series") continue;
            var seriesIds = component.Select(i => i.Id).ToHashSet();
            var children = items.Where(i => seriesIds.Contains(i.SeriesId)).ToArray();
            foreach (var season in children.Where(i => i.Kind == "Season"))
                Assign(season, season.SeasonNumber.HasValue ? key + ":s:" + season.SeasonNumber : null, key);
            // Every episode gets the shared series key, even episodes available in only one library.
            foreach (var episode in children.Where(i => i.Kind == "Episode"))
                Assign(episode, null, key);
            foreach (var episodes in children.Where(i => i.Kind == "Episode" && i.Eligible
                         && i.SeasonNumber.HasValue && i.EpisodeNumber.HasValue)
                     .GroupBy(i => (i.SeasonNumber, i.EpisodeNumber, i.EpisodeEnd)))
            {
                var versions = episodes.ToList();
                if (HasConflicts(versions))
                {
                    warnings.Add($"Skipped conflicting episode IDs: {title.Name} S{episodes.Key.SeasonNumber}E{episodes.Key.EpisodeNumber}.");
                    continue;
                }
                var episodeKey = key + $":s:{episodes.Key.SeasonNumber}:e:{episodes.Key.EpisodeNumber}:{episodes.Key.EpisodeEnd}";
                if (versions.Select(i => i.LibraryId).Distinct().Count() > 1)
                    AddGroup(versions, episodeKey, "Episode", versions[0].Name, key);
            }
        }
        return new(assignments.Values.ToArray(), groups.ToArray(), warnings.ToArray());

        void Assign(CatalogItem item, string? presentation, string? series) => assignments[item.Id] =
            new(item.Id, item.Path, item.Kind, presentation, series, item.Label, item.Preference, Signature(item));
        void AddGroup(List<CatalogItem> members, string presentation, string kind, string name, string? series)
        {
            groups.Add(new(presentation, kind, name, members.OrderBy(i => i.Preference).ThenBy(i => i.Id).Select(i => i.Id).ToArray()));
            foreach (var member in members) Assign(member, presentation, series);
        }
    }

    private static IEnumerable<string> Tokens(CatalogItem item) => Providers
        .Where(p => item.ProviderIds.TryGetValue(p, out var id) && ValidProviderId(p, id))
        .Select(p => item.Kind + ":" + p.ToLowerInvariant() + ":" + item.ProviderIds[p].Trim().ToLowerInvariant());

    private static bool HasConflicts(IEnumerable<CatalogItem> items) => Providers.Any(p => items
        .Select(i => i.ProviderIds.TryGetValue(p, out var id) ? id?.Trim().ToLowerInvariant() ?? "" : "")
        .Where(id => ValidProviderId(p, id)).Distinct().Skip(1).Any());

    private static bool ValidProviderId(string provider, string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        id = id.Trim();
        if (provider == "Imdb") return id.StartsWith("tt", StringComparison.OrdinalIgnoreCase)
            && long.TryParse(id.AsSpan(2), out var imdb) && imdb > 0;
        return long.TryParse(id, out var number) && number > 0;
    }
    private static string NormalizeOrder(string value) => value.Trim().ToLowerInvariant() switch
    { "" or "aired" or "airdate" => "airdate", var order => order };

    public static string Signature(CatalogItem item) => Key(JsonSerializer.Serialize(new object?[]
    {
        item.Kind, item.Path, item.SeriesId, item.SeasonNumber, item.EpisodeNumber, item.EpisodeEnd,
        NormalizeOrder(item.DisplayOrder), Tokens(item).Order(StringComparer.Ordinal).ToArray()
    }));

    private static string Key(string identity) => "librarybridge:v1:" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
}
