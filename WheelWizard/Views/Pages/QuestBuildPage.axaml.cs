using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using WheelWizard.Recomp;
using WheelWizard.Recomp.Domain;
using WheelWizard.Services;
using WheelWizard.Settings;
using WheelWizard.Shared.DependencyInjection;
using WheelWizard.Shared.MessageTranslations;
using WheelWizard.Views.Popups.Generic;

namespace WheelWizard.Views.Pages;

/// <summary>
/// Builds the player's own game for the WiiCompiled Quest app. The app ships no game code, so the
/// game is compiled here from this installation's translation against the kit inside the app's APK,
/// and saved as a .wcgame the headset imports, always with the game files so the headset needs
/// nothing else. The APK is fetched from the GitHub release this installation came from, the only
/// one whose kit fits it, so the player never handles it.
/// </summary>
public partial class QuestBuildPage : UserControlBase
{
    /// <summary>
    /// The games offered, the first being the default. The pack only travels with Retro Rewind, and
    /// Retro Rewind on the headset needs it, so choosing the game decides the pack too.
    /// </summary>
    private static readonly IReadOnlyList<(string DisplayName, RecompGame Game)> OfferedGames =
    [
        ("Mario Kart Wii + Retro Rewind", RecompGame.RetroRewind),
        ("Mario Kart Wii", RecompGame.Base),
    ];

    // How much of the progress bar the app download takes before the build itself starts.
    private const int DownloadPercentShare = 10;

    private bool _supported;

    [Inject]
    private ISettingsManager SettingsService { get; set; } = null!;

    [Inject]
    private IRecompEnvironment? RecompEnvironment { get; set; }

    [Inject]
    private IRecompInstallService? RecompInstallService { get; set; }

    public QuestBuildPage()
    {
        InitializeComponent();

        HeadsetDropdown.ItemsSource = RecompQuestHeadsets.Offered.Select(headset => headset.DisplayName()).ToList();
        HeadsetDropdown.SelectedIndex = RecompQuestHeadsets.Offered.ToList().IndexOf(SavedHeadset());
        HeadsetDropdown.SelectionChanged += (_, _) =>
            SettingsService.Set(SettingsService.RECOMP_QUEST_HEADSET, SelectedHeadset.ToSettingValue());
        GameDropdown.ItemsSource = OfferedGames.Select(game => game.DisplayName).ToList();
        GameDropdown.SelectedIndex = 0;

        AttachedToVisualTree += (_, _) => RecompOperationCoordinator.Changed += RefreshOperationState;
        DetachedFromVisualTree += (_, _) => RecompOperationCoordinator.Changed -= RefreshOperationState;
        RefreshOperationState();
        _ = RefreshAvailabilityAsync();
    }

    private RecompGame SelectedGame =>
        GameDropdown.SelectedIndex >= 0 && GameDropdown.SelectedIndex < OfferedGames.Count
            ? OfferedGames[GameDropdown.SelectedIndex].Game
            : OfferedGames[0].Game;

    private RecompQuestHeadset SavedHeadset() =>
        RecompQuestHeadsets.FromSettingValue(SettingsService.Get<string>(SettingsService.RECOMP_QUEST_HEADSET));

    private RecompQuestHeadset SelectedHeadset =>
        HeadsetDropdown.SelectedIndex >= 0 && HeadsetDropdown.SelectedIndex < RecompQuestHeadsets.Offered.Count
            ? RecompQuestHeadsets.Offered[HeadsetDropdown.SelectedIndex]
            : RecompQuestHeadset.ModernQuest;

    private void RefreshOperationState() => Dispatcher.UIThread.Post(() => IsEnabled = !RecompOperationCoordinator.IsBusy);

    /// <summary>
    /// Says upfront when this installation cannot build for the Quest at all, instead of letting the
    /// player pick options for a build that is going to be refused.
    /// </summary>
    private async Task RefreshAvailabilityAsync()
    {
        var message = await UnavailableReasonAsync();
        _supported = message is null;
        UnavailableBanner.IsVisible = !_supported;
        UnavailableText.Text = message ?? string.Empty;
        HeadsetSection.IsEnabled = _supported;
        IncludeSection.IsEnabled = _supported;
        BuildButton.IsEnabled = _supported;
    }

    private async Task<string?> UnavailableReasonAsync()
    {
        if (RecompInstallService is null || RecompEnvironment?.Backend.Kind != RecompBackendKind.OpenXR)
            return "Building for Meta Quest needs the WiiCompiled VR backend. Choose it in Settings, then come back.";
        if (!RecompInstallService.IsInstalled)
            return "WiiCompiled VR is not installed yet. Install it from Settings, then come back.";
        if (!await RecompInstallService.SupportsQuestBuildAsync())
            return "This WiiCompiled VR installation cannot build for Meta Quest yet. Update WiiCompiled, then try again.";
        return null;
    }

    private async void Build_OnClick(object? sender, RoutedEventArgs e)
    {
        if (RecompInstallService is null || !_supported)
            return;

        // The app is looked up before the player names an output file, so a build that cannot even
        // start does not cost them a save dialog.
        var found = await RecompInstallService.FindQuestAppAsync(SelectedHeadset);
        if (found.IsFailure)
        {
            await new MessageBoxWindow()
                .SetMessageType(MessageBoxWindow.MessageType.Warning)
                .SetTitleText("The Quest app could not be found")
                .SetInfoText(found.Error.Message)
                .ShowDialog();
            return;
        }
        var app = found.Value;

        var game = SelectedGame;
        var retroRewind = game == RecompGame.RetroRewind;
        RecompInstallService.SelectGame(game);

        var outputPath = await FilePickerHelper.SaveFileAsync(
            "Save the Quest game",
            [new FilePickerFileType("Quest game") { Patterns = ["*.wcgame"] }],
            retroRewind ? "RetroRewind.wcgame" : "MarioKartWii.wcgame"
        );
        if (outputPath is null)
            return;

        var result = await RunQuestBuildAsync(RecompInstallService, app, outputPath, includeModContent: retroRewind);
        if (result is null)
            return;
        if (result.IsFailure)
        {
            MessageTranslationHelper.ShowMessage(result.Error);
            return;
        }

        var (package, apkPath) = result.Value;
        var size = package.SizeBytes >= 1_000_000_000 ? $"{package.SizeBytes / 1e9:F1} GB" : $"{package.SizeBytes / 1e6:F0} MB";
        var openFolder = await new YesNoWindow()
            .SetMainText("The Quest game is ready")
            .SetExtraText(
                $"{Path.GetFileName(package.Path)} ({size}) was saved. Copy it to the headset, for example into its "
                    + "Download folder over USB, then open the WiiCompiled Quest app and press Import from computer."
                    + $"\n\nThe game fits {app.DisplayName}. If the headset runs another version, install "
                    + $"{Path.GetFileName(apkPath)} from {Path.GetDirectoryName(apkPath)} on it first."
            )
            .SetButtonText("Open folder", "Close")
            .AwaitAnswer();
        var folder = Path.GetDirectoryName(package.Path);
        if (openFolder && Directory.Exists(folder))
            FilePickerHelper.OpenFolderInFileManager(folder);
    }

    /// <summary>
    /// Fetches the app, then runs the build with the game files, both under the shared operation
    /// lease with one cancellable progress window. Returns the package and the APK it was built
    /// against, or <see langword="null"/> when another game or WiiCompiled operation holds the lease.
    /// </summary>
    private async Task<OperationResult<(RecompQuestPackageEvent Package, string ApkPath)>?> RunQuestBuildAsync(
        IRecompInstallService installService,
        RecompQuestApp app,
        string outputPath,
        bool includeModContent
    )
    {
        IDisposable operation;
        try
        {
            operation = RecompOperationCoordinator.Acquire();
        }
        catch (IOException)
        {
            await new MessageBoxWindow()
                .SetTitleText("WiiCompiled is busy")
                .SetInfoText(RecompOperationCoordinator.BusyMessage)
                .ShowDialog();
            return null;
        }

        using var lease = operation;
        using var cancellationTokenSource = new CancellationTokenSource();
        const string goal = "Building the game for Meta Quest";
        var progressWindow = new ProgressWindow(goal)
            .SetGoal(goal)
            .SetExtraText(t("progress.this_may_take_a_while"))
            .SetCancellationTokenSource(cancellationTokenSource);

        // One bar for both phases: the download takes its first stretch.
        var downloadProgress = new Progress<RecompInstallProgress>(update =>
        {
            progressWindow.SetExtraText(update.Message);
            progressWindow.UpdateProgress(update.Percent * DownloadPercentShare / 100);
        });
        var buildProgress = new Progress<RecompInstallProgress>(update =>
        {
            progressWindow.SetExtraText(update.Message);
            progressWindow.UpdateProgress(DownloadPercentShare + update.Percent * (100 - DownloadPercentShare) / 100);
        });

        progressWindow.Show();
        try
        {
            var download = await installService.DownloadQuestAppAsync(app, downloadProgress, cancellationTokenSource.Token);
            if (download.IsFailure)
                return Cancelled(progressWindow, cancellationTokenSource) ?? download.Error;
            var apkPath = download.Value;

            var result = await installService.BuildForQuestAsync(
                apkPath,
                outputPath,
                includeGameFiles: true,
                includeModContent,
                buildProgress,
                cancellationTokenSource.Token
            );
            if (result.IsFailure)
                return Cancelled(progressWindow, cancellationTokenSource) ?? result.Error;
            return Ok((result.Value, apkPath));
        }
        catch (OperationCanceledException)
        {
            return CancelledResult;
        }
        finally
        {
            progressWindow.Close();
        }
    }

    private static OperationError CancelledResult =>
        Fail("The Quest build was cancelled.", MessageTranslation.Warning_RecompOperationCancelled);

    private static OperationError? Cancelled(ProgressWindow progressWindow, CancellationTokenSource cancellationTokenSource) =>
        progressWindow.WasCancellationRequested || cancellationTokenSource.IsCancellationRequested ? CancelledResult : null;
}
