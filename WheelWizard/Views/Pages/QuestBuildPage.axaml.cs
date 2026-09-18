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
/// and saved as a .wcgame the headset imports.
/// </summary>
public partial class QuestBuildPage : UserControlBase
{
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
        ShowApkPath(RememberedApkPath());
        UpdateGameDependentControls();

        AttachedToVisualTree += (_, _) => RecompOperationCoordinator.Changed += RefreshOperationState;
        DetachedFromVisualTree += (_, _) => RecompOperationCoordinator.Changed -= RefreshOperationState;
        RefreshOperationState();
        _ = RefreshAvailabilityAsync();
    }

    private bool IsRetroRewind => GameDropdown.SelectedIndex == 1;

    private void RefreshOperationState() => Dispatcher.UIThread.Post(() => IsEnabled = !RecompOperationCoordinator.IsBusy);

    /// <summary>
    /// The APK the last build used. The kit inside it is what the game is compiled against, so the
    /// same file serves every build until the Quest app itself is replaced.
    /// </summary>
    private string RememberedApkPath()
    {
        var remembered = SettingsService.Get<string>(SettingsService.RECOMP_QUEST_APK) ?? string.Empty;
        return File.Exists(remembered) ? remembered : string.Empty;
    }

    private void ShowApkPath(string path)
    {
        ApkPathText.Text = string.IsNullOrEmpty(path)
            ? "The app holds the game kit your game is built against, so it decides how the game is compiled."
            : path;
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
            UpdateGameDependentControls();
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

        SettingsService.Set(SettingsService.RECOMP_QUEST_APK, path);
        ShowApkPath(path);
    }

    private async void Build_OnClick(object? sender, RoutedEventArgs e)
    {
        if (RecompInstallService is null || !_supported)
            return;

        var apkPath = RememberedApkPath();
        if (string.IsNullOrEmpty(apkPath))
        {
            await new MessageBoxWindow()
                .SetTitleText("Choose the Quest app first")
                .SetInfoText(
                    "The game is compiled against the game kit inside the Quest app, so this needs the app's APK file. "
                        + "It is the same file you installed on the headset."
                )
                .ShowDialog();
            return;
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
            apkPath,
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

        var package = result.Value;
        var size = package.SizeBytes >= 1_000_000_000 ? $"{package.SizeBytes / 1e9:F1} GB" : $"{package.SizeBytes / 1e6:F0} MB";
        var openFolder = await new YesNoWindow()
            .SetMainText("The Quest game is ready")
            .SetExtraText(
                $"{Path.GetFileName(package.Path)} ({size}) was saved. Copy it to the headset, for example into its "
                    + "Download folder over USB, then open the WiiCompiled Quest app and press Import from computer."
                    + (package.IncludesGameFiles ? string.Empty : "\n\nIt has no game files, so extract your disc in the Quest app too.")
            )
            .SetButtonText("Open folder", "Close")
            .AwaitAnswer();
        var folder = Path.GetDirectoryName(package.Path);
        if (openFolder && Directory.Exists(folder))
            FilePickerHelper.OpenFolderInFileManager(folder);
    }

    /// <summary>
    /// Runs the build under the shared operation lease with a cancellable progress window, or returns
    /// <see langword="null"/> when another game or WiiCompiled operation holds the lease.
    /// </summary>
    private async Task<OperationResult<RecompQuestPackageEvent>?> RunQuestBuildAsync(
        IRecompInstallService installService,
        string apkPath,
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
        var progress = new Progress<RecompInstallProgress>(update =>
        {
            progressWindow.SetExtraText(update.Message);
            progressWindow.UpdateProgress(update.Percent);
        });

        progressWindow.Show();
        try
        {
            var result = await installService.BuildForQuestAsync(
                apkPath,
                outputPath,
                includeGameFiles,
                includeModContent,
                progress,
                cancellationTokenSource.Token
            );
            if (result.IsFailure && (progressWindow.WasCancellationRequested || cancellationTokenSource.IsCancellationRequested))
                return Fail("The Quest build was cancelled.", MessageTranslation.Warning_RecompOperationCancelled);
            return result;
        }
        catch (OperationCanceledException)
        {
            return Fail("The Quest build was cancelled.", MessageTranslation.Warning_RecompOperationCancelled);
        }
        finally
        {
            progressWindow.Close();
        }
    }
}
