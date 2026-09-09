using WheelWizard.CustomDistributions;
using WheelWizard.Models.Enums;
using WheelWizard.Mods;
using WheelWizard.Recomp.Domain;
using WheelWizard.Services;
using WheelWizard.Services.Launcher;
using WheelWizard.Shared.MessageTranslations;
using WheelWizard.Views.Popups.Generic;

namespace WheelWizard.Recomp;

/// <summary>
/// Exposes the Mario Kart Wii recomp as a regular WheelWizard launcher. It owns nothing but the UI
/// wiring: every decision about installing, updating and launching lives in <see cref="IRecompInstallService"/>,
/// which in turn drives the recomp's setup executable. A shared lease covers preparation, setup,
/// and the full game session across both backends and launcher processes.
/// </summary>
public class RecompLauncher(
    IRecompInstallService installService,
    ICustomDistributionSingletonService customDistributions,
    IModsLaunchService modsLaunchService,
    IRecompDolphinDataService dolphinData,
    IRecompEnvironment environment
) : ILauncher
{
    private RecompGame _game = RecompGame.RetroRewind;
    public string GameName => _game == RecompGame.Base ? "Base game" : "Retro Rewind";
    public string BackendName => environment.Backend.DisplayName;
    public string GameTitle => $"{GameName} — {BackendName}";

    public RecompLauncher WithGame(RecompGame game)
    {
        _game = game;
        installService.SelectGame(game);
        return this;
    }

    public async Task<OperationResult> Launch()
    {
        using var cancellationTokenSource = new CancellationTokenSource();
        var goal = t("progress.updating_recomp");
        var progressWindow = new ProgressWindow(goal)
            .SetGoal(goal)
            .SetExtraText(t("progress.this_may_take_a_while"))
            .SetCancellationTokenSource(cancellationTokenSource);
        var progress = new Progress<RecompInstallProgress>(update =>
        {
            progressWindow.SetExtraText(update.Message);
            progressWindow.UpdateProgress(update.Percent);
        });

        try
        {
            using var operation = RecompOperationCoordinator.Acquire();
            RetroRewindContentTransaction.Recover(new Testably.Abstractions.RealFileSystem(), PathManager.RiivolutionWhWzFolderPath);
            var nand = dolphinData.ApplyNandToRecompConfig();
            if (nand.IsFailure)
                return nand;
            var targetFolderPath = PathManager.PatchesFolderPath;
            var clearTargetFolder = false;
            if (_game == RecompGame.RetroRewind && modsLaunchService.ShouldAskToClearTargetFolder(targetFolderPath))
            {
                clearTargetFolder = await new YesNoWindow()
                    .SetButtonText(t("action.delete"), t("action.keep"))
                    .SetMainText(t("question.launch_clear_mods_found.title"))
                    .SetExtraText(t("question.launch_clear_patches_found.extra"))
                    .AwaitAnswer();
            }

            if (_game == RecompGame.RetroRewind)
            {
                var modsLaunchResult = await modsLaunchService.PrepareModsForLaunch(targetFolderPath, clearTargetFolder);
                if (modsLaunchResult.IsFailure)
                    return modsLaunchResult.Error;
            }

            progressWindow.Show();
            var reconciliation = await installService.ReconcileForLaunchAsync(progress, cancellationTokenSource.Token);
            if (reconciliation.IsFailure)
                return IsCancellationRequested(progressWindow, cancellationTokenSource)
                    ? CancellationWarning("WiiCompiled launch preparation was cancelled.")
                    : reconciliation;

            if (IsCancellationRequested(progressWindow, cancellationTokenSource))
                return CancellationWarning("WiiCompiled launch preparation was cancelled.");

            // Reconciliation is the only cancellable progress phase. The launch call is
            // awaited through game exit.
            progressWindow.SetCancellationTokenSource(null);
            progressWindow.Close();
            return await installService.LaunchAsync(CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            return CancellationWarning("WiiCompiled launch preparation was cancelled.");
        }
        catch (IOException ex)
        {
            return Fail(ex.Message);
        }
        finally
        {
            progressWindow.Close();
        }
    }

    public async Task<OperationResult> Install()
    {
        IDisposable operation;
        try
        {
            operation = RecompOperationCoordinator.Acquire();
        }
        catch (IOException ex)
        {
            return Fail(ex.Message);
        }
        using var lease = operation;
        var nandChoice = await AskForDolphinNandChoiceAsync();
        if (nandChoice.IsFailure)
            return nandChoice;

        var installResult = await RunSetupAsync(t("progress.installing_recomp"));
        if (installResult.IsFailure)
            return installResult;

        // The recomp compiled without any NAND knowledge; pointing its Config.toml at the chosen
        // Wii data is what makes the choice real, and it applies on the very next launch.
        return dolphinData.ApplyNandToRecompConfig();
    }

    /// <summary>
    /// The setup service selects its full-release or targeted-repair operation after Retro Rewind
    /// has been brought current below, and only after --check-products says work is needed.
    /// Re-applying the NAND setting afterwards keeps an installation in sync when the user moved or
    /// re-linked their Dolphin data since the last one.
    /// </summary>
    public async Task<OperationResult> Update()
    {
        IDisposable operation;
        try
        {
            operation = RecompOperationCoordinator.Acquire();
        }
        catch (IOException ex)
        {
            return Fail(ex.Message);
        }
        using var lease = operation;
        var updateResult = await RunSetupAsync(t("progress.updating_recomp"));
        if (updateResult.IsFailure)
            return updateResult;

        return dolphinData.ApplyNandToRecompConfig();
    }

    public async Task<WheelWizardStatus> GetCurrentStatus()
    {
        // While a launch or setup operation is running, the session only got this far by being
        // ready
        if (installService.OperationInFlight || RecompOperationCoordinator.IsBusy)
            return WheelWizardStatus.Ready;

        try
        {
            var retroRewindStatus = await customDistributions.RetroRewind.GetCurrentStatusAsync();
            if (_game == RecompGame.Base && installService.IsInstalled)
                return await installService.GetCurrentStatusAsync();
            if (retroRewindStatus.IsFailure)
                return WheelWizardStatus.NoServer;

            switch (retroRewindStatus.Value)
            {
                case WheelWizardStatus.ConfigNotFinished:
                case WheelWizardStatus.NotInstalled:
                case WheelWizardStatus.OutOfDate:
                case WheelWizardStatus.NoServer:
                    return retroRewindStatus.Value;
            }

            var recompStatus = await installService.GetCurrentStatusAsync();
            if (recompStatus is not (WheelWizardStatus.Ready or WheelWizardStatus.NoServerButInstalled))
                return recompStatus;

            return
                retroRewindStatus.Value == WheelWizardStatus.NoServerButInstalled || recompStatus == WheelWizardStatus.NoServerButInstalled
                ? WheelWizardStatus.NoServerButInstalled
                : WheelWizardStatus.Ready;
        }
        catch (Exception)
        {
            return WheelWizardStatus.NoServer;
        }
    }

    /// <summary>
    /// A fresh install is the moment the user decides where WiiCompiled's Wii data comes from: their
    /// existing Dolphin NAND, a copy of it, or nothing. Dolphin advises against other programs using
    /// its NAND in place, so that trade-off is the user's call, not a silent default. An installed
    /// setup keeps whatever was chosen before; the Recomp Settings page is where that changes later.
    /// </summary>
    private async Task<OperationResult> AskForDolphinNandChoiceAsync()
    {
        if (
            installService.IsInstalled
            || File.Exists(PathManager.RecompConfigFilePath)
            || Directory.Exists(PathManager.RecompPrivateNandFolderPath)
        )
            return Ok();

        var sourceNand = await Task.Run(() => dolphinData.SourceNandFolderPath);
        if (sourceNand is null)
        {
            // There is no Dolphin data to offer, so the recomp simply starts with its own.
            dolphinData.SetSharingEnabled(false);
            dolphinData.SetCopyEnabled(false);
            return Ok();
        }

        var useDolphinData = await new YesNoWindow()
            .SetMainText(t("question.recomp_use_dolphin_nand.title"))
            .SetExtraText(t("question.recomp_use_dolphin_nand.extra"))
            .AwaitAnswer();
        if (!useDolphinData)
        {
            dolphinData.SetSharingEnabled(false);
            dolphinData.SetCopyEnabled(false);
            return Ok();
        }

        var copyNand = await new YesNoWindow()
            .SetMainText(t("question.recomp_nand_mode.title"))
            .SetExtraText(t("question.recomp_nand_mode.extra"))
            .SetButtonText(t("action.recomp_nand_copy"), t("action.recomp_nand_share"))
            .AwaitAnswer();
        if (!copyNand)
        {
            dolphinData.SetCopyEnabled(false);
            dolphinData.SetSharingEnabled(true);
            return Ok();
        }

        var copyWindow = new ProgressWindow(t("progress.recomp_copying_nand")).SetGoal(t("progress.recomp_copying_nand"));
        copyWindow.Show();
        try
        {
            var copyResult = await Task.Run(dolphinData.CopyNandForRecomp);
            if (copyResult.IsFailure)
                return copyResult;
        }
        finally
        {
            copyWindow.Close();
        }

        // Flipped only after the copy durably exists, so a failed copy can never leave the settings
        // pointing at a NAND that is not there.
        dolphinData.SetCopyEnabled(true);
        dolphinData.SetSharingEnabled(false);
        return Ok();
    }

    private async Task<OperationResult> RunSetupAsync(string goal)
    {
        using var cancellationTokenSource = new CancellationTokenSource();

        // Constructed on the UI thread, so Progress<T> marshals every update back to it for us.
        var progressWindow = new ProgressWindow(goal)
            .SetGoal(goal)
            .SetExtraText(t("progress.this_may_take_a_while"))
            .SetCancellationTokenSource(cancellationTokenSource);

        var progress = new Progress<RecompInstallProgress>(update =>
        {
            progressWindow.SetExtraText(update.Message);
            progressWindow.UpdateProgress(update.Percent);
        });

        try
        {
            progressWindow.Show();

            var retroRewindResult = await EnsureRetroRewindCurrentAsync(progressWindow, cancellationTokenSource.Token);
            if (retroRewindResult.IsFailure)
                return IsCancellationRequested(progressWindow, cancellationTokenSource)
                    ? CancellationWarning("WiiCompiled installation was cancelled.")
                    : retroRewindResult.Error;

            var retroRewindCommitted = retroRewindResult.Value;
            if (!retroRewindCommitted && IsCancellationRequested(progressWindow, cancellationTokenSource))
                return CancellationWarning("WiiCompiled installation was cancelled.");

            if (retroRewindCommitted)
            {
                // RR is now durably published. The paired recomp reconciliation is the second
                // half of that commit and must finish even if Cancel raced the RR commit point.
                // Disable the button at this commit barrier: reporting "cancelled" after both
                // halves complete would invite an unnecessary retry of a successful operation.
                progressWindow.SetCancellationTokenSource(null);
            }

            // The Retro Rewind commit is a barrier: once it is durable, the matching recomp
            // check (and any repair it demands) must run to completion, so it is deliberately
            // handed an uncancellable token. Whatever the backend reports is the truth.
            var installResult = await installService.InstallAsync(
                progress,
                ConfirmOfflineInstallAsync,
                retroRewindCommitted ? CancellationToken.None : cancellationTokenSource.Token
            );

            // A successful terminal result wins a race with Cancel. Otherwise, a cancellation that
            // was still allowed at this point is a user-controlled warning, not an unknown error.
            return installResult.IsFailure && !retroRewindCommitted && IsCancellationRequested(progressWindow, cancellationTokenSource)
                ? CancellationWarning("WiiCompiled installation was cancelled.")
                : installResult;
        }
        catch (OperationCanceledException)
        {
            return CancellationWarning("WiiCompiled installation was cancelled.");
        }
        finally
        {
            progressWindow.Close();
        }
    }

    private static bool IsCancellationRequested(ProgressWindow progressWindow, CancellationTokenSource cancellationTokenSource) =>
        progressWindow.WasCancellationRequested || cancellationTokenSource.IsCancellationRequested;

    private static OperationError CancellationWarning(string message) => Fail(message, MessageTranslation.Warning_RecompOperationCancelled);

    /// <summary>
    /// Asked by the install service only when a Retro Rewind build is needed and the Retro-WFC payload
    /// service is down. Offline-only is a real choice, not a silent downgrade: the game plays, online
    /// does not, and the next update after the service returns rebuilds with online play automatically.
    /// </summary>
    private static Task<bool> ConfirmOfflineInstallAsync() =>
        new YesNoWindow()
            .SetButtonText(t("action.recomp_install_offline"), t("action.cancel"))
            .SetMainText(t("question.recomp_retro_wfc_unavailable.title"))
            .SetExtraText(t("question.recomp_retro_wfc_unavailable.extra"))
            .AwaitAnswer();

    private async Task<OperationResult<bool>> EnsureRetroRewindCurrentAsync(
        ProgressWindow progressWindow,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var status = await customDistributions.RetroRewind.GetCurrentStatusAsync();
        if (status.IsFailure)
            return status.Error;

        var requiresCommit = status.Value is WheelWizardStatus.NotInstalled or WheelWizardStatus.OutOfDate;
        OperationResult result = status.Value switch
        {
            WheelWizardStatus.Ready or WheelWizardStatus.NoServerButInstalled => Ok(),
            WheelWizardStatus.NotInstalled => await customDistributions.RetroRewind.InstallAsync(progressWindow),
            WheelWizardStatus.OutOfDate => await customDistributions.RetroRewind.UpdateAsync(progressWindow),
            WheelWizardStatus.ConfigNotFinished => Fail(t("message_warning.not_find_game.extra")),
            WheelWizardStatus.NoServer => Fail("Retro Rewind could not be checked or installed because its update service is unavailable."),
            _ => Fail("Retro Rewind is not ready for WiiCompiled."),
        };

        if (result.IsFailure)
            return result.Error;
        if (!requiresCommit && (progressWindow.WasCancellationRequested || cancellationToken.IsCancellationRequested))
            return Fail("Retro Rewind update was cancelled.");
        return Ok(requiresCommit);
    }
}
