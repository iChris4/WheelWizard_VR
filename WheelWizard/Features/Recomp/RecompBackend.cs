using WheelWizard.ApplicationData;

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
    private static readonly System.IO.Abstractions.IPath NativePath = new Testably.Abstractions.RealFileSystem().Path;

    public const string VrProductId = "wiicompiled-openxr-vr";
    public static readonly RecompBackend Normal = new(
        RecompBackendKind.Normal,
        "Recomp",
        "WiiCompiled",
        RecompReleaseResolver.RepositoryOwner,
        RecompReleaseResolver.RepositoryName,
        null
    );
    public static readonly RecompBackend OpenXR = new(
        RecompBackendKind.OpenXR,
        "RecompVR",
        "WiiCompiled OpenXR VR",
        "iChris4",
        "Wiicompiled_VR",
        VrProductId
    );

    public static RecompBackend For(RecompBackendKind kind) => kind == RecompBackendKind.OpenXR ? OpenXR : Normal;

    public string Root(string appData) => NativePath.Combine(appData, FolderName);

    public string Install(string appData) => NativePath.Combine(Root(appData), "Install");

    public string UserData(string appData) => NativePath.Combine(Root(appData), "UserData");

    public string Config(string appData) => NativePath.Combine(UserData(appData), "Config.toml");
}

/// <summary>Independent of SettingsManager to avoid a configuration/DI dependency cycle.</summary>
public sealed class RecompBackendSelection(IApplicationDataLocation applicationData, IRecompPaths paths)
{
    public RecompBackendKind Kind { get; private set; }
    public RecompBackend Backend => RecompBackend.For(Kind);

    // The normal Config.toml lives wherever IRecompPaths puts it on this platform; VR is Windows-only.
    public string ConfigPath => Kind == RecompBackendKind.OpenXR ? Backend.Config(applicationData.DirectoryPath) : paths.ConfigFilePath;

    public void Restore(bool normal, bool vr) =>
        Kind =
            vr && OperatingSystem.IsWindows() ? RecompBackendKind.OpenXR
            : normal && RecompPlatform.IsSupported ? RecompBackendKind.Normal
            : RecompBackendKind.Dolphin;
}
