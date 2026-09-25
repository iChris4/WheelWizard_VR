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
/// and saved as a .wcgame the headset imports. The APK is fetched from the GitHub release this
/// installation came from, the only one whose kit fits it, so the player normally never handles it;
/// choosing a file by hand remains possible for a build of the app that was never published.
/// </summary>
public partial class QuestBuildPage : UserControlBase
{
    private const string AutomaticAppText =
        "Downloaded from the WiiCompiled release this installation came from, so the game always fits the app.";

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

        GameDropdown.ItemsSource = new[] { "Mario Kart Wii", "Retro Rewind" };
        GameDropdown.SelectedIndex = 0;
        GameDropdown.SelectionChanged += (_, _) => UpdateGameDependentControls();
        IncludeGameFiles.IsChecked = true;
        ShowQuestAppSource();
        UpdateGameDependentControls();

        AttachedToVisualTree += (_, _) => RecompOperationCoordinator.Changed += RefreshOperationState;
        DetachedFromVisualTree += (_, _) => RecompOperationCoordinator.Changed -= RefreshOperationState;
        RefreshOperationState();
        _ = RefreshAvailabilityAsync();
    }

    private bool IsRetroRewind => GameDropdown.SelectedIndex == 1;

    private void RefreshOperationState() => Dispatcher.UIThread.Post(() => IsEnabled = !RecompOperationCoordinator.IsBusy);

    /// <summary>
    /// An APK the player chose instead of the downloaded app, or empty for the usual automatic case.
    /// A file that has gone since reads as automatic again.
    /// </summary>
    private string ChosenApkPath()
    {
        var remembered = SettingsService.Get<string>(SettingsService.RECOMP_QUEST_APK_OVERRIDE) ?? string.Empty;
        return File.Exists(remembered) ? remembered : string.Empty;
    }

    private void ShowQuestAppSource()
    {
        var chosen = ChosenApkPath();
        var automatic = string.IsNullOrEmpty(chosen);
        ApkTitleText.Text = automatic ? "Downloaded for you" : "Chosen by hand";
        ApkPathText.Text = automatic ? AutomaticAppText : chosen;
        UseDownloadedAppButton.IsVisible = !automatic;
    }

    private void UpdateGameDependentControls()
    {
        // Only the modded game has a pack to carry.
        IncludeModContent.IsEnabled = IsRetroRewind;
        if (!IsRetroRewind)
            IncludeModContent.IsChecked = false;
    }

    /// <summary>
    /// Says upfront when this installation cannot build for the Quest at all, instead of letting the
    /// player pick files for a build that is going to be refused.
    /// </summary>
    private async Task RefreshAvailabilityAsync()
    {
        var message = await UnavailableReasonAsync();
        _supported = message is null;
        UnavailableBanner.IsVisible = !_supported;
        UnavailableText.Text = message ?? string.Empty;
        GameSection.IsEnabled = _supported;
        IncludeSection.IsEnabled = _supported;
        ApkSection.IsEnabled = _supported;
        BuildButton.IsEnabled = _supported;
        if (_supported)
        {
            UpdateGameDependentControls();
            await DescribeDownloadedAppAsync();
        }
    }

    /// <summary>
    /// Names the app a build will fetch, so the player sees which version the headset must run
    /// before building. GitHub being unreachable is not worth a warning here: the build says so.
    /// </summary>
    private async Task DescribeDownloadedAppAsync()
    {
        if (RecompInstallService is null || !string.IsNullOrEmpty(ChosenApkPath()))
            return;

        var found = await RecompInstallService.FindQuestAppAsync();
        if (found.IsFailure || !string.IsNullOrEmpty(ChosenApkPath()))
            return;

        var app = found.Value;
        ApkPathText.Text =
            $"{app.FileName} from WiiCompiled {app.ReleaseTag} is downloaded when you build, so the game always fits the app. "
            + $"The headset must run {app.DisplayName}.";
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

    private async void ChooseApk_OnClick(object? sender, RoutedEventArgs e)
    {
        var path = await FilePickerHelper.OpenSingleFileAsync(
            "Select the WiiCompiled Quest app",
            [new FilePickerFileType("Quest app (APK)") { Patterns = ["*.apk"] }]
        );
        if (path is null)
            return;

        SettingsService.Set(SettingsService.RECOMP_QUEST_APK_OVERRIDE, path);
        ShowQuestAppSource();
    }

    private async void UseDownloadedApp_OnClick(object? sender, RoutedEventArgs e)
    {
        SettingsService.Set(SettingsService.RECOMP_QUEST_APK_OVERRIDE, string.Empty);
        ShowQuestAppSource();
        if (_supported)
            await DescribeDownloadedAppAsync();
    }

    private async void Build_OnClick(object? sender, RoutedEventArgs e)
    {
        if (RecompInstallService is null || !_supported)
            return;

        // The app is looked up before the player names an output file, so a build that cannot even
        // start does not cost them a save dialog.
        var chosenApk = ChosenApkPath();
        RecompQuestApp? app = null;
        if (string.IsNullOrEmpty(chosenApk))
        {
            var found = await RecompInstallService.FindQuestAppAsync();
            if (found.IsFailure)
            {
                await new MessageBoxWindow()
                    .SetMessageType(MessageBoxWindow.MessageType.Warning)
                    .SetTitleText("The Quest app could not be found")
                    .SetInfoText(found.Error.Message)
                    .ShowDialog();
                return;
            }
            app = found.Value;
        }

        var retroRewind = IsRetroRewind;
        var includeModContent = retroRewind && IncludeModContent.IsChecked == true;
        RecompInstallService.SelectGame(retroRewind ? RecompGame.RetroRewind : RecompGame.Base);

        var outputPath = await FilePickerHelper.SaveFileAsync(
            "Save the Quest game",
            [new FilePickerFileType("Quest game") { Patterns = ["*.wcgame"] }],
            retroRewind ? "RetroRewind.wcgame" : "MarioKartWii.wcgame"
        );
        if (outputPath is null)
            return;

        var result = await RunQuestBuildAsync(
            RecompInstallService,
            app,
            chosenApk,
            outputPath,
            IncludeGameFiles.IsChecked == true,
            includeModContent
        );
        if (result is null)
            return;
        if (result.IsFailure)
        {
            MessageTranslationHelper.ShowMessage(result.Error);
            return;
        }

        var (package, apkPath) = result.Value;
        var size = package.SizeBytes >= 1_000_000_000 ? $"{package.SizeBytes / 1e9:F1} GB" : $"{package.SizeBytes / 1e6:F0} MB";
        var appNote = app is null
            ? $"\n\nThe game fits the Quest app you chose, {Path.GetFileName(apkPath)}."
            : $"\n\nThe game fits {app.DisplayName}. If the headset runs another version, install "
                + $"{Path.GetFileName(apkPath)} from {Path.GetDirectoryName(apkPath)} on it first.";
        var openFolder = await new YesNoWindow()
            .SetMainText("The Quest game is ready")
            .SetExtraText(
                $"{Path.GetFileName(package.Path)} ({size}) was saved. Copy it to the headset, for example into its "
                    + "Download folder over USB, then open the WiiCompiled Quest app and press Import from computer."
                    + (package.IncludesGameFiles ? string.Empty : "\n\nIt has no game files, so extract your disc in the Quest app too.")
                    + appNote
            )
            .SetButtonText("Open folder", "Close")
            .AwaitAnswer();
        var folder = Path.GetDirectoryName(package.Path);
        if (openFolder && Directory.Exists(folder))
            FilePickerHelper.OpenFolderInFileManager(folder);
    }

    /// <summary>
    /// Fetches the app when none was chosen by hand, then runs the build, both under the shared
    /// operation lease with one cancellable progress window. Returns the package and the APK it was
    /// built against, or <see langword="null"/> when another game or WiiCompiled operation holds the lease.
    /// </summary>
    private async Task<OperationResult<(RecompQuestPackageEvent Package, string ApkPath)>?> RunQuestBuildAsync(
        IRecompInstallService installService,
        RecompQuestApp? app,
        string chosenApkPath,
        string outputPath,
        bool includeGameFiles,
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

        // One bar for both phases: the download takes its first stretch only when there is one.
        var downloadShare = app is null ? 0 : DownloadPercentShare;
        var downloadProgress = new Progress<RecompInstallProgress>(update =>
        {
            progressWindow.SetExtraText(update.Message);
            progressWindow.UpdateProgress(update.Percent * downloadShare / 100);
        });
        var buildProgress = new Progress<RecompInstallProgress>(update =>
        {
            progressWindow.SetExtraText(update.Message);
            progressWindow.UpdateProgress(downloadShare + update.Percent * (100 - downloadShare) / 100);
        });

        progressWindow.Show();
        try
        {
            var apkPath = chosenApkPath;
            if (app is not null)
            {
                var download = await installService.DownloadQuestAppAsync(app, downloadProgress, cancellationTokenSource.Token);
                if (download.IsFailure)
                    return Cancelled(progressWindow, cancellationTokenSource) ?? download.Error;
                apkPath = download.Value;
            }

            var result = await installService.BuildForQuestAsync(
                apkPath,
                outputPath,
                includeGameFiles,
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
