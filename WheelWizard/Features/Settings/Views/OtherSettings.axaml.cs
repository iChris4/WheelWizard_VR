using System.IO.Abstractions;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using WheelWizard.ApplicationData;
using WheelWizard.CustomDistributions;
using WheelWizard.CustomDistributions.Presentation;
using WheelWizard.Recomp;
using WheelWizard.Settings;
using WheelWizard.Shared.Desktop.Storage;
using WheelWizard.Shared.MessageTranslations;
using WheelWizard.Views.Dialogs;

namespace WheelWizard.Settings.Views;

public partial class OtherSettings : UserControl
{
    private readonly IFileSystem fileSystem;

    private readonly bool _settingsAreDisabled;
    private bool _loadingModes;

    private IFilePickerService FilePicker { get; }

    private ICustomDistributionPaths DistributionPaths { get; }

    private ICustomDistributionSingletonService CustomDistributionSingletonService { get; }

    private ISettingsManager SettingsService { get; }

    private IApplicationDataLocation ApplicationData { get; }

    public OtherSettings(
        IFileSystem fileSystem,
        IFilePickerService filePicker,
        ICustomDistributionPaths distributionPaths,
        ICustomDistributionSingletonService customDistributionSingletonService,
        ISettingsManager settingsService,
        IApplicationDataLocation applicationData
    )
    {
        ApplicationData = applicationData;
        this.fileSystem = fileSystem;
        FilePicker = filePicker;
        DistributionPaths = distributionPaths;
        CustomDistributionSingletonService = customDistributionSingletonService;
        SettingsService = settingsService;
        InitializeComponent();
        _settingsAreDisabled = !SettingsService.DolphinPathsSetupCorrectly();
        DisabledWarningText.IsVisible = _settingsAreDisabled;

        // Recomp can be enabled with only a game image configured. Disable the Dolphin-only
        // controls individually so the recomp switch never becomes trapped behind Dolphin setup.
        LaunchRrOnStartup.IsEnabled = !_settingsAreDisabled;
        DolphinReinstallButton.IsEnabled = !_settingsAreDisabled;
        OpenGameFolderButton.IsEnabled = !_settingsAreDisabled && fileSystem.Directory.Exists(DistributionPaths.RootFolderPath);
        OpenSaveFolderButton.IsEnabled = !_settingsAreDisabled;
        if (!_settingsAreDisabled)
            LoadSettings();
        ForceLoadSettings();
        RefreshRetroRewindVersion();

        // Attach event handlers after loading settings to avoid unwanted triggers
        LaunchRrOnStartup.IsCheckedChanged += ClickLaunchRrOnStartup;
        EnableRecomp.IsCheckedChanged += ClickEnableRecomp;
        EnableRecompVR.IsCheckedChanged += ClickEnableRecomp;
        AttachedToVisualTree += (_, _) => RecompOperationCoordinator.Changed += RefreshModeAvailability;
        DetachedFromVisualTree += (_, _) => RecompOperationCoordinator.Changed -= RefreshModeAvailability;
        RefreshModeAvailability();
    }

    private void LoadSettings()
    {
        // Only loads when the settings are not disabled (aka when the paths are set up correctly)
        LaunchRrOnStartup.IsChecked = SettingsService.Get<bool>(SettingsService.LAUNCH_RR_ON_STARTUP);
        OpenGameFolderButton.IsEnabled = fileSystem.Directory.Exists(DistributionPaths.RootFolderPath);
        OpenSaveFolderButton.IsEnabled = fileSystem.Directory.Exists(DistributionPaths.SaveFolderPath);
    }

    private void ForceLoadSettings()
    {
        // Always loads

        // The recomp only runs where a setup backend exists for it, so elsewhere the whole section stays hidden.
        var recompSupported = RecompPlatform.IsSupported;
        RecompSectionLabel.IsVisible = recompSupported;
        RecompBorder.IsVisible = recompSupported;
        // The OpenXR VR backend runs on D3D12, so it is only offered on Windows.
        RecompVrBorder.IsVisible = recompSupported && OperatingSystem.IsWindows();
        if (recompSupported)
            RefreshModes();
    }

    private void RefreshRetroRewindVersion()
    {
        var version = CustomDistributionSingletonService.RetroRewind.GetCurrentVersion()?.ToString() ?? t("state.unknown");
        RetroRewindVersionText.Text = t("helper_text.installed_version", new { version = version });
    }

    private void ClickLaunchRrOnStartup(object? sender, RoutedEventArgs e)
    {
        SettingsEditing.Set(SettingsService, SettingsService.LAUNCH_RR_ON_STARTUP, LaunchRrOnStartup.IsChecked == true);
    }

    // The two switches are one choice between the normal backend, the VR one, and Dolphin when both are off.
    private void ClickEnableRecomp(object? sender, RoutedEventArgs e)
    {
        if (_loadingModes)
            return;
        try
        {
            var vr = ReferenceEquals(sender, EnableRecompVR);
            var setting = vr ? SettingsService.ENABLE_RECOMP_VR : SettingsService.ENABLE_RECOMP;
            var enabled = (vr ? EnableRecompVR : EnableRecomp).IsChecked == true;
            if (!SettingsService.Set(setting, enabled) && setting.SaveError is { } error)
                MessageTranslationHelper.ShowMessage(Fail(error));
        }
        catch (IOException)
        {
            ShowBusyMessage();
        }
        finally
        {
            RefreshModes();
        }
    }

    private void RefreshModes()
    {
        _loadingModes = true;
        EnableRecomp.IsChecked = SettingsService.Get<bool>(SettingsService.ENABLE_RECOMP);
        EnableRecompVR.IsChecked = SettingsService.Get<bool>(SettingsService.ENABLE_RECOMP_VR);
        _loadingModes = false;
    }

    private void RefreshModeAvailability() =>
        Dispatcher.UIThread.Post(() =>
        {
            EnableRecomp.IsEnabled = EnableRecompVR.IsEnabled = !RecompOperationCoordinator.IsBusy;
        });

    private static void ShowBusyMessage() =>
        new MessageBoxWindow().SetTitleText("WiiCompiled is busy").SetInfoText(RecompOperationCoordinator.BusyMessage).Show();

    private async void Reinstall_RetroRewind(object sender, RoutedEventArgs e)
    {
        IDisposable operation;
        try
        {
            operation = RecompOperationCoordinator.Acquire(ApplicationData.DirectoryPath);
        }
        catch (IOException)
        {
            ShowBusyMessage();
            return;
        }
        using var lease = operation;
        var progressWindow = new ProgressWindow();
        OperationResult result;
        try
        {
            progressWindow.Show();
            result = await CustomDistributionSingletonService.RetroRewind.ReinstallAsync(progressWindow);
        }
        catch (Exception exception)
        {
            result = Fail(exception);
        }
        finally
        {
            progressWindow.Close();
            RefreshRetroRewindVersion();
        }

        if (result.IsFailure)
            MessageTranslationHelper.ShowMessage(result.Error);
    }

    private void OpenSaveFolder_OnClick(object? sender, RoutedEventArgs e)
    {
        FilePicker.OpenFolderInFileManager(DistributionPaths.SaveFolderPath);
    }

    private void GameFileFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (!fileSystem.Directory.Exists(DistributionPaths.RootFolderPath))
            return;

        FilePicker.OpenFolderInFileManager(DistributionPaths.RootFolderPath);
    }
}
