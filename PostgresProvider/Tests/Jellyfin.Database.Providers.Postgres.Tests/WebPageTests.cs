using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace Jellyfin.Database.Providers.Postgres.Tests;

/// <summary>
/// The dashboard page is a single embedded HTML file, so nothing at compile time checks that the ids the
/// script reaches for exist, that every tab has a pane, that every message has English text, or that
/// the embedded resource is the one the plugin advertises. These tests cover exactly that.
/// </summary>
public sealed class WebPageTests
{
    private static readonly string Html = File.ReadAllText(
        Path.Combine(TestEnvironment.PluginSourceDirectory, "Web", "configurationPage.html"));

    [Fact]
    public void EveryElementIdUsedByTheScriptExists()
    {
        var defined = Matches("id=\"([A-Za-z][A-Za-z0-9_-]*)\"");
        var used = Matches("getElementById\\('([A-Za-z][A-Za-z0-9_-]*)'\\)");

        var missing = used.Except(defined).OrderBy(id => id, StringComparer.Ordinal).ToArray();

        Assert.Empty(missing);
    }

    [Fact]
    public void EveryTabHasItsPane()
    {
        var tabs = Matches("data-tab=\"([a-z]+)\"").OrderBy(tab => tab, StringComparer.Ordinal).ToArray();
        var panes = Matches("id=\"pg-tab-([a-z]+)\"").OrderBy(pane => pane, StringComparer.Ordinal).ToArray();

        Assert.NotEmpty(tabs);
        Assert.Equal(tabs, panes);
    }

    [Fact]
    public void EveryDashboardMessageHasEnglishText()
    {
        const string declaration = "var STRINGS = ";
        var dictionaryStart = Html.IndexOf(declaration, StringComparison.Ordinal);
        var dictionaryEnd = Html.IndexOf("function t(", dictionaryStart, StringComparison.Ordinal);

        Assert.True(dictionaryStart > 0 && dictionaryEnd > dictionaryStart,
            "Could not locate the dashboard message dictionary.");

        using var dictionary = JsonDocument.Parse(Html[(dictionaryStart + declaration.Length)..dictionaryEnd].TrimEnd().TrimEnd(';'));
        var english = dictionary.RootElement.EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.GetString(), StringComparer.Ordinal);

        Assert.NotEmpty(english);
        Assert.All(english.Values, value => Assert.False(string.IsNullOrWhiteSpace(value)));
        Assert.Contains("<html lang=\"en\">", Html, StringComparison.Ordinal);

        // The lookbehind keeps calls like createElement('th') from being mistaken for t('th').
        var used = Matches("(?<![A-Za-z0-9_])t\\('([a-z0-9_]+)'").ToHashSet(StringComparer.Ordinal);
        var untranslated = used.Except(english.Keys).OrderBy(key => key, StringComparer.Ordinal).ToArray();

        Assert.Empty(untranslated);
    }

    /// <summary>
    /// Jellyfin serves the panel from the embedded resource named by <c>GetPages</c>
    /// (<c>&lt;namespace&gt;.Web.configurationPage.html</c>); if the file is renamed without renaming the
    /// resource, the dashboard shows an empty page with no compile error.
    /// </summary>
    [Fact]
    public void ConfigurationPageResourceIsEmbedded()
    {
        var expected = string.Concat(typeof(PostgresPlugin).Namespace, ".Web.configurationPage.html");

        Assert.Contains(expected, typeof(PostgresPlugin).Assembly.GetManifestResourceNames());
    }

    [Fact]
    public void ConnectionPasswordIsContainedInANamedForm()
    {
        Assert.Matches(
            "<form id=\"pgConnectionForm\"[\\s\\S]*?<input is=\"emby-input\" type=\"password\" id=\"pgPassword\" name=\"pg-password\"[\\s\\S]*?</form>",
            Html);
    }

    [Fact]
    public void SQLiteValidationIsDeferredUntilRollbackIsRequested()
    {
        Assert.Contains("apiGet('/SqliteValidation')", Html, StringComparison.Ordinal);

        var statusStart = Html.IndexOf("function loadStatus()", StringComparison.Ordinal);
        var statusEnd = Html.IndexOf("function switchToTab", statusStart, StringComparison.Ordinal);
        Assert.True(statusStart >= 0 && statusEnd > statusStart, "Could not isolate loadStatus.");
        Assert.DoesNotContain("SqliteValidation", Html[statusStart..statusEnd], StringComparison.Ordinal);
    }

    private static HashSet<string> Matches(string pattern)
        => Regex.Matches(Html, pattern, RegexOptions.CultureInvariant)
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

}
