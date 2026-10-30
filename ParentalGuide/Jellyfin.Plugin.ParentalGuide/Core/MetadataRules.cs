using System.Text;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.ParentalGuide.Configuration;

namespace Jellyfin.Plugin.ParentalGuide.Core;

public static partial class MetadataRules
{
    public static readonly string[] CategoryNames =
    [
        "Sex & Nudity", "Violence & Gore", "Profanity",
        "Alcohol, Drugs & Smoking", "Frightening & Intense Scenes"
    ];

    private static readonly Dictionary<string, int> Severities = new(StringComparer.OrdinalIgnoreCase)
    {
        ["None"] = 0, ["Mild"] = 1, ["Moderate"] = 2, ["Severe"] = 3
    };

    private static readonly Dictionary<string, int> RatingLevels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["G"] = 0, ["TV-Y"] = 0, ["TV-G"] = 0,
        ["TV-Y7"] = 1, ["TV-Y7-FV"] = 1, ["PG"] = 1, ["TV-PG"] = 1,
        ["PG-13"] = 2, ["TV-14"] = 2, ["R"] = 3, ["TV-MA"] = 3, ["NC-17"] = 4
    };

    [GeneratedRegex(@"^tt\d+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ImdbPattern();
    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
    [GeneratedRegex(@"^(?:US|USA)[:-]")]
    private static partial Regex UsPrefix();

    public static bool ValidImdbId(string? id) => id is not null && ImdbPattern().IsMatch(id);
    public static string NormalizeTitle(string title) =>
        string.Concat(title.Normalize(NormalizationForm.FormKC).Where(char.IsLetterOrDigit)).ToLowerInvariant();
    public static bool HasCertification(string? rating) => rating?.Trim().ToUpperInvariant() is
        not (null or "" or "N/A" or "NR" or "NOT RATED" or "UNRATED");
    public static bool KnownSeverity(string? severity) => severity is not null && Severities.ContainsKey(severity);

    public static int? RatingLevel(string? rating)
    {
        var normalized = UsPrefix().Replace((rating ?? "").Trim().ToUpperInvariant().Replace('_', '-'), "");
        return RatingLevels.TryGetValue(normalized, out var level) ? level : null;
    }

    public static string? Infer(IReadOnlyList<GuideCategory> categories, bool series, int minimumVotes)
    {
        var eligible = categories.Where(c => CategoryNames.Contains(c.Name, StringComparer.Ordinal) &&
                KnownSeverity(c.Severity) && c.Votes >= minimumVotes)
            .GroupBy(c => c.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Max(c => Severities[c.Severity]), StringComparer.Ordinal);
        if (eligible.Values.Any(level => level == 3))
        {
            return series ? "TV-MA" : "R";
        }

        // Missing/low-vote categories may hide severe content; never infer a lower rating.
        if (eligible.Count != CategoryNames.Length)
        {
            return null;
        }

        return (series ? new[] { "TV-G", "TV-PG", "TV-14" } : ["G", "PG", "PG-13"])[eligible.Values.Max()];
    }

    public static MetadataPlan Plan(MediaTitle item, OmdbTitle? match, IReadOnlyList<GuideCategory> guide,
        PluginConfiguration options, string? lastInferredRating)
    {
        var candidate = HasCertification(match?.Rating) ? match!.Rating!.Trim() : null;
        var inferred = false;
        if (candidate is null && options.InferFromParentsGuide &&
            (!HasCertification(item.Rating) || options.OnlyUpgradeRatings ||
             (lastInferredRating is not null && string.Equals(item.Rating, lastInferredRating, StringComparison.OrdinalIgnoreCase))))
        {
            candidate = Infer(guide, item.IsSeries, options.ParentsGuideMinimumVotes);
            inferred = candidate is not null;
        }

        if (candidate is not null && HasCertification(item.Rating) && options.OnlyUpgradeRatings)
        {
            var previousLevel = RatingLevel(item.Rating);
            var candidateLevel = RatingLevel(candidate);
            if (previousLevel is null || candidateLevel is null || candidateLevel <= previousLevel)
            {
                candidate = null;
                inferred = false;
            }
        }

        var tags = new List<string>();
        if (options.AddLanguageTags && match?.Languages is { } languages)
        {
            foreach (var part in languages.Split(','))
            {
                var language = Whitespace().Replace(part, " ").Trim();
                if (language.Length > 0 && !language.Equals("N/A", StringComparison.OrdinalIgnoreCase) &&
                    !language.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
                {
                    tags.Add("Lang:" + language);
                }
            }
        }

        if (options.AddParentsGuideTags)
        {
            // Vote thresholds apply only to inference, never to category tags.
            foreach (var category in guide.Where(c => CategoryNames.Contains(c.Name, StringComparer.Ordinal) && KnownSeverity(c.Severity)))
            {
                var severity = Severities.Keys.First(s => s.Equals(category.Severity, StringComparison.OrdinalIgnoreCase));
                tags.Add($"Parents Guide: {category.Name} - {severity}");
            }

            if (inferred)
            {
                tags.Add("Rating Source: IMDb Parents Guide");
            }
        }

        var existing = item.Tags.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new MetadataPlan(candidate ?? item.Rating, tags.Where(existing.Add).ToArray(), inferred);
    }
}
