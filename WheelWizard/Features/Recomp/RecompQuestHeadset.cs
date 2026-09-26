namespace WheelWizard.Recomp;

/// <summary>
/// Which Meta Quest the app is built for. A WiiCompiled release publishes one app per kind, and a
/// game built against one app's kit does not load in the other: the original Quest's app targets the
/// Snapdragon 835, whose processor the modern app's code crashes on.
/// </summary>
public enum RecompQuestHeadset
{
    /// <summary>Quest 2, 3, 3S and Pro: <c>WiiCompiledVR-Quest-&lt;version&gt;.apk</c>.</summary>
    ModernQuest,

    /// <summary>The original Quest: <c>WiiCompiledVR-Quest1-&lt;version&gt;.apk</c>.</summary>
    OriginalQuest,
}

public static class RecompQuestHeadsets
{
    /// <summary>The headsets in the order they are offered; the first is the default.</summary>
    public static IReadOnlyList<RecompQuestHeadset> Offered { get; } = [RecompQuestHeadset.ModernQuest, RecompQuestHeadset.OriginalQuest];

    /// <summary>What the settings file stores for a headset, and the default when it stores nothing usable.</summary>
    public const string ModernSettingValue = "modern";
    public const string OriginalSettingValue = "quest1";

    public static string ToSettingValue(this RecompQuestHeadset headset) =>
        headset == RecompQuestHeadset.OriginalQuest ? OriginalSettingValue : ModernSettingValue;

    /// <summary>Anything but the original Quest's value reads as the modern app, the safe default.</summary>
    public static RecompQuestHeadset FromSettingValue(string? value) =>
        string.Equals(value?.Trim(), OriginalSettingValue, StringComparison.OrdinalIgnoreCase)
            ? RecompQuestHeadset.OriginalQuest
            : RecompQuestHeadset.ModernQuest;

    public static string DisplayName(this RecompQuestHeadset headset) =>
        headset == RecompQuestHeadset.OriginalQuest ? "Original Quest" : "Quest 2, 3, 3S and Pro";

    /// <summary>How messages call the app for that headset.</summary>
    public static string AppNoun(this RecompQuestHeadset headset) =>
        headset == RecompQuestHeadset.OriginalQuest ? "Quest 1 app" : "Quest app";
}
