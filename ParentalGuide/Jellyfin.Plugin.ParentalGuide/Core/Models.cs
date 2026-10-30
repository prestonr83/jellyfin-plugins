using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.ParentalGuide.Core;

public sealed record MediaTitle(Guid Id, string Name, int? Year, bool IsSeries,
    string? ImdbId, string? Rating, IReadOnlyList<string> Tags)
{
    // Exclude ratings/tags: our own edits must never invalidate completion.
    public string Identity => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        string.Join('\0', Name, Year?.ToString(CultureInfo.InvariantCulture) ?? "",
            IsSeries.ToString(), ImdbId?.Trim().ToLowerInvariant() ?? ""))));
}

public sealed record GuideCategory(string Name, string Severity, int Votes);
public sealed record OmdbTitle(string? ImdbId, string? Rating, string? Languages);
public sealed record MetadataPlan(string? Rating, IReadOnlyList<string> TagsToAdd, bool Inferred)
{
    public bool ChangesRating(MediaTitle item) => !string.Equals(Rating, item.Rating, StringComparison.Ordinal);
    public bool HasChanges(MediaTitle item) => ChangesRating(item) || TagsToAdd.Count > 0;
}

public interface IMediaLibrary
{
    IReadOnlyList<Guid> GetItemIds(CancellationToken cancellationToken);
    MediaTitle? GetItem(Guid id);
    Task ApplyAsync(MediaTitle current, MetadataPlan plan, CancellationToken cancellationToken);
}

public interface IMetadataSource
{
    Task<OmdbTitle?> FindAsync(MediaTitle item, CancellationToken cancellationToken);
    Task<IReadOnlyList<GuideCategory>> GetGuideAsync(string imdbId, CancellationToken cancellationToken);
}

public class ProviderException(string message) : Exception(message);
public sealed class FatalProviderException(string message) : ProviderException(message);
