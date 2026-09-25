using System.Text.RegularExpressions;
using WheelWizard.GitHub.Domain;
using WheelWizard.Recomp.Domain;

namespace WheelWizard.Recomp;

/// <summary>
/// Picks the Meta Quest app that fits an installation out of a GitHub releases listing. A Quest build
/// compiles against the game kit inside the app's APK, and the kit only matches the WiiCompiled release
/// it shipped with, so the app is looked up by the installed version rather than by recency: a newer
/// release's app would only be refused by the headset.
/// </summary>
public static class RecompQuestAppResolver
{
    /// <summary>How the release script names the app, <c>WiiCompiledVR-Quest-&lt;version&gt;.apk</c>.</summary>
    private static readonly Regex AppFileName = new(
        @"^WiiCompiledVR-Quest-(?<version>\d+\.\d+\.\d+)\.apk$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    );

    /// <summary>
    /// Returns the app published with the release whose tag is <paramref name="installedVersion"/>, or
    /// <see langword="null"/> when no release carries that version or the release ships no APK.
    /// </summary>
    public static RecompQuestApp? FindForInstalledVersion(IEnumerable<GithubRelease>? releases, string? installedVersion)
    {
        if (releases is null || !RecompVersion.TryParse(installedVersion, out var installed))
            return null;

        foreach (var release in releases)
        {
            if (!RecompVersion.TryParse(release.TagName, out var version) || version.ComparePrecedenceTo(installed) != 0)
                continue;

            // The named asset is the app; any other APK is only a fallback for a hand-made release.
            var asset =
                release.Assets.FirstOrDefault(candidate => AppFileName.IsMatch(candidate.Name))
                ?? release.Assets.FirstOrDefault(candidate => candidate.Name.EndsWith(".apk", StringComparison.OrdinalIgnoreCase));
            if (asset is null || string.IsNullOrWhiteSpace(asset.BrowserDownloadUrl))
                return null;

            var match = AppFileName.Match(asset.Name);
            return new(
                release.TagName,
                match.Success ? match.Groups["version"].Value : null,
                asset.Name,
                asset.BrowserDownloadUrl,
                asset.Size
            );
        }

        return null;
    }
}
