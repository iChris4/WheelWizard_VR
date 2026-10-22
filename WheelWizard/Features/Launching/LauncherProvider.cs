using WheelWizard.Recomp;
using WheelWizard.Settings;

namespace WheelWizard.Launching;

/// <summary>
/// Resolves the launcher the Home page should drive. The recomp is a beta (Windows and Linux) that, when
/// opted into, replaces the Dolphin/Retro Rewind frontend entirely; which launcher that decision
/// selects lives here, so no view has to re-derive it.
/// </summary>
public interface ILauncherProvider
{
    ILauncher GetActiveLauncher();
    IReadOnlyList<ILauncher> GetLaunchers() => [GetActiveLauncher()];
}

public class LauncherProvider(ISettingsManager settings, Func<RrLauncher> createRetroRewind, Func<RecompLauncher?> createRecomp)
    : ILauncherProvider
{
    // Windows resolves an install service per launcher, so each one keeps its own game. The Linux
    // service is shared, so Linux keeps the single Retro Rewind launcher.
    public IReadOnlyList<ILauncher> GetLaunchers() =>
        settings.IsRecompModeActive() && !RecompPlatform.IsLinux
            ? [CreateRecomp().WithGame(RecompGame.RetroRewind), CreateRecomp().WithGame(RecompGame.Base)]
            : [GetActiveLauncher()];

    public ILauncher GetActiveLauncher() => settings.IsRecompModeActive() ? CreateRecomp() : createRetroRewind();

    private RecompLauncher CreateRecomp() =>
        createRecomp() ?? throw new InvalidOperationException("The recomp launcher is unavailable on this platform.");
}
