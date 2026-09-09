using WheelWizard.Recomp;
using WheelWizard.Settings;

namespace WheelWizard.Services.Launcher;

/// <summary>
/// Resolves the launcher the Home page should drive. The recomp is a Windows-only beta that, when
/// opted into, replaces the Dolphin/Retro Rewind frontend entirely; which launcher that decision
/// selects lives here, so no view has to re-derive it.
/// </summary>
public interface ILauncherProvider
{
    ILauncher GetActiveLauncher();
    IReadOnlyList<ILauncher> GetLaunchers() => [GetActiveLauncher()];
}

public class LauncherProvider(ISettingsManager settings, IServiceProvider serviceProvider) : ILauncherProvider
{
    public IReadOnlyList<ILauncher> GetLaunchers() =>
        settings.IsRecompModeActive()
            ?
            [
                serviceProvider.GetRequiredService<RecompLauncher>().WithGame(RecompGame.RetroRewind),
                serviceProvider.GetRequiredService<RecompLauncher>().WithGame(RecompGame.Base),
            ]
            : [GetActiveLauncher()];

    public ILauncher GetActiveLauncher() =>
        settings.IsRecompModeActive()
            ? serviceProvider.GetRequiredService<RecompLauncher>()
            : serviceProvider.GetRequiredService<RrLauncher>();
}
