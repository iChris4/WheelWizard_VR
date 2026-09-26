using System.Text.RegularExpressions;
using WheelWizard.GitHub.Domain;
using WheelWizard.Recomp.Domain;

namespace WheelWizard.Recomp;

/// <summary>
/// Picks the Meta Quest app that fits an installation out of a GitHub releases listing. A Quest build
/// compiles against the game kit inside the app's APK, and the kit only matches the WiiCompiled release
/// it shipped with, so the app is looked up by the installed version rather than by recency: a newer
/// release's app would only be refused by the headset. A release carries one app per headset kind,
/// told apart by name, and a game built for one does not load in the other.
/// </summary>
public static class RecompQuestAppResolver
{
    /// <summary>How the release script names the modern app, <c>WiiCompiledVR-Quest-&lt;version&gt;.apk</c>.</summary>
    private static readonly Regex ModernAppFileName = new(
        @"^WiiCompiledVR-Quest-(?<version>\d+\.\d+\.\d+)\.apk$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    );

    /// <summary>The original Quest's app, <c>WiiCompiledVR-Quest1-&lt;version&gt;.apk</c>.</summary>
    private static readonly Regex OriginalAppFileName = new(
        @"^WiiCompiledVR-Quest1-(?<version>\d+\.\d+\.\d+)\.apk$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    );

    /// <summary>
    /// Returns the app for <paramref name="headset"/> published with the release whose tag is
    /// <paramref name="installedVersion"/>, or <see langword="null"/> when no release carries that
    /// version or the release ships no such app.
    /// </summary>
    public static RecompQuestApp? FindForInstalledVersion(
        IEnumerable<GithubRelease>? releases,
        string? installedVersion,
        RecompQuestHeadset headset = RecompQuestHeadset.ModernQuest
    )
    {
        if (releases is null || !RecompVersion.TryParse(installedVersion, out var installed))
            return null;

        var named = headset == RecompQuestHeadset.OriginalQuest ? OriginalAppFileName : ModernAppFileName;
        foreach (var release in releases)
        {
            if (!RecompVersion.TryParse(release.TagName, out var version) || version.ComparePrecedenceTo(installed) != 0)
                continue;

            // The named asset is the app. Any other APK is only a fallback for a hand-made release of
            // the modern app; the original Quest's must say so in its name, since a modern app taken
            // by mistake would crash on that headset rather than be refused.
            var asset = release.Assets.FirstOrDefault(candidate => named.IsMatch(candidate.Name));
            if (asset is null && headset == RecompQuestHeadset.ModernQuest)
            {
                asset = release.Assets.FirstOrDefault(candidate =>
                    candidate.Name.EndsWith(".apk", StringComparison.OrdinalIgnoreCase) && !OriginalAppFileName.IsMatch(candidate.Name)
                );
            }
            if (asset is null || string.IsNullOrWhiteSpace(asset.BrowserDownloadUrl))
                return null;

            var match = named.Match(asset.Name);
            return new(
                release.TagName,
                match.Success ? match.Groups["version"].Value : null,
                asset.Name,
                asset.BrowserDownloadUrl,
                asset.Size,
                headset
            );
        }

        return null;
    }
}
