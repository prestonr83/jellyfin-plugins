using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.ParentalGuide.Configuration;

public sealed class PluginConfiguration : BasePluginConfiguration
{
    public string OmdbApiKey { get; set; } = string.Empty;
    public bool DryRun { get; set; } = true;
    public bool ForceFullScanOnNextRun { get; set; }
    public int MaximumItems { get; set; }
    public bool TitleFallback { get; set; } = true;
    public bool AddLanguageTags { get; set; } = true;
    public bool AddParentsGuideTags { get; set; } = true;
    public bool InferFromParentsGuide { get; set; }
    public int ParentsGuideMinimumVotes { get; set; } = 3;
    public bool OnlyUpgradeRatings { get; set; } = true;
    public int RequestTimeoutSeconds { get; set; } = 30;
    public int MaximumRetries { get; set; } = 4;
    public double RetryBaseDelaySeconds { get; set; } = 1;
    public double RetryMaximumDelaySeconds { get; set; } = 60;
    public double OmdbDelaySeconds { get; set; }
    public double ImdbDelaySeconds { get; set; } = 0.25;

    // Freeze settings for a run so dashboard edits affect only subsequent runs.
    public PluginConfiguration Snapshot() => (PluginConfiguration)MemberwiseClone();

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(OmdbApiKey))
        {
            throw new InvalidOperationException("Set an OMDb API key in the Parental Guide plugin settings first.");
        }

        if (MaximumItems < 0 || ParentsGuideMinimumVotes < 1 ||
            RequestTimeoutSeconds is < 1 or > 600 || MaximumRetries is < 0 or > 10 ||
            !double.IsFinite(RetryBaseDelaySeconds) || RetryBaseDelaySeconds <= 0 ||
            !double.IsFinite(RetryMaximumDelaySeconds) || RetryMaximumDelaySeconds < RetryBaseDelaySeconds ||
            RetryMaximumDelaySeconds > 3600 ||
            !double.IsFinite(OmdbDelaySeconds) || OmdbDelaySeconds is < 0 or > 3600 ||
            !double.IsFinite(ImdbDelaySeconds) || ImdbDelaySeconds is < 0 or > 3600)
        {
            throw new InvalidOperationException("Invalid Parental Guide limits, vote threshold, timeout, or retry settings.");
        }
    }
}
