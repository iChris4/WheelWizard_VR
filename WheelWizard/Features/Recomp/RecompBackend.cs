using WheelWizard.Services;

namespace WheelWizard.Recomp;

public enum RecompBackendKind
{
    Dolphin,
    Normal,
    OpenXR,
}

public enum RecompGame
{
    RetroRewind,
    Base,
}

/// <summary>Immutable identity and layout; versions are compared only within this repository.</summary>
public sealed record RecompBackend(
    RecompBackendKind Kind,
    string FolderName,
    string DisplayName,
    string RepositoryOwner,
    string RepositoryName,
    string? ProductId
)
{
    public const string VrProductId = "wiicompiled-openxr-vr";
    public static readonly RecompBackend Normal = new(RecompBackendKind.Normal, "Recomp", "WiiCompiled", "patchzyy", "Wiicompiled", null);
    public static readonly RecompBackend OpenXR = new(
        RecompBackendKind.OpenXR,
        "RecompVR",
        "WiiCompiled OpenXR VR",
        "iChris4",
        "Wiicompiled_VR",
        VrProductId
    );

    public static RecompBackend For(RecompBackendKind kind) => kind == RecompBackendKind.OpenXR ? OpenXR : Normal;

    public string Root(string appData) => Path.Combine(appData, FolderName);

    public string Install(string appData) => Path.Combine(Root(appData), "Install");

    public string UserData(string appData) => Path.Combine(Root(appData), "UserData");

    public string Config(string appData) => Path.Combine(UserData(appData), "Config.toml");
}

/// <summary>Independent of SettingsManager to avoid a configuration/DI dependency cycle.</summary>
public sealed class RecompBackendSelection
{
    public RecompBackendKind Kind { get; private set; }
    public RecompBackend Backend => RecompBackend.For(Kind);
    public string ConfigPath => Backend.Config(PathManager.WheelWizardAppdataPath);

    public void Restore(bool normal, bool vr) =>
        Kind =
            !OperatingSystem.IsWindows() ? RecompBackendKind.Dolphin
            : vr ? RecompBackendKind.OpenXR
            : normal ? RecompBackendKind.Normal
            : RecompBackendKind.Dolphin;
}
