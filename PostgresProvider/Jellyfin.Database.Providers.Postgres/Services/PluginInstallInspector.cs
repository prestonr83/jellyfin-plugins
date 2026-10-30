using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace Jellyfin.Database.Providers.Postgres.Services;

/// <summary>
/// Looks for leftovers of other versions of this plugin next to the one that is running. Jellyfin discovers the
/// database provider assembly from every folder under <c>plugins</c>, even from folders marked as deleted, so a
/// stale copy can be the one that actually serves queries. Nothing in the plugin used to say so.
/// </summary>
public static class PluginInstallInspector
{
    /// <summary>
    /// Describes every sibling folder of the running plugin that holds another copy of it.
    /// </summary>
    /// <param name="assemblyLocation">Path of the running plugin assembly.</param>
    /// <param name="loadedVersion">Version of the running assembly.</param>
    /// <returns>One actionable sentence per leftover folder; empty when the installation is clean.</returns>
    public static IReadOnlyList<string> Inspect(string? assemblyLocation, Version loadedVersion)
    {
        var warnings = new List<string>();
        if (string.IsNullOrWhiteSpace(assemblyLocation))
        {
            return warnings;
        }

        var current = new DirectoryInfo(Path.GetDirectoryName(assemblyLocation) ?? string.Empty);
        var parent = current.Parent;
        if (!current.Exists || parent is null)
        {
            return warnings;
        }

        // Folders are named "<plugin name>_<version>"; the name part is what identifies this plugin.
        var separator = current.Name.LastIndexOf('_');
        var prefix = separator > 0 ? current.Name[..separator] : current.Name;

        foreach (var sibling in parent.EnumerateDirectories(prefix + "_*"))
        {
            if (string.Equals(sibling.FullName, current.FullName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var versionText = sibling.Name[(prefix.Length + 1)..];
            var status = ReadStatus(sibling.FullName);
            var hasAssembly = sibling.GetFiles("Jellyfin.Database.Providers.Postgres.dll").Length > 0;
            var newer = Version.TryParse(versionText, out var parsed) && parsed > loadedVersion;

            var detail = string.Create(
                CultureInfo.InvariantCulture,
                $"Another copy of the plugin exists in '{sibling.Name}' (status: {status}{(hasAssembly ? ", contains the assembly" : string.Empty)}). ");
            var advice = newer
                ? $"It is NEWER than the running version ({loadedVersion}): Jellyfin is loading an older version. "
                : "Jellyfin may load the wrong assembly, preventing plugin changes from taking effect. ";
            warnings.Add(detail + advice + "Stop Jellyfin, delete that folder, and start it again.");
        }

        return warnings;
    }

    private static string ReadStatus(string folder)
    {
        try
        {
            var meta = Path.Combine(folder, "meta.json");
            if (!File.Exists(meta))
            {
                return "no meta.json";
            }

            using var document = JsonDocument.Parse(File.ReadAllText(meta));
            return document.RootElement.TryGetProperty("status", out var status) ? status.GetString() ?? "desconocido" : "desconocido";
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return "ilegible";
        }
    }
}
