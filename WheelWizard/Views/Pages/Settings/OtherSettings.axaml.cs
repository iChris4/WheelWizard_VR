using Avalonia.Interactivity;
using Avalonia.Threading;
using WheelWizard.CustomDistributions;
using WheelWizard.Recomp;
using WheelWizard.Services;
using WheelWizard.Settings;
using WheelWizard.Shared.DependencyInjection;
using WheelWizard.Views.Popups.Generic;

namespace WheelWizard.Views.Pages.Settings;

public partial class OtherSettings : UserControlBase
{
    private readonly bool _settingsAreDisabled;
    private bool _loadingModes;

    [Inject]
    private ICustomDistributionSingletonService CustomDistributionSingletonService { get; set; } = null!;

    [Inject]
    private ISettingsManager SettingsService { get; set; } = null!;

    public OtherSettings()
    {
        InitializeComponent();
        _settingsAreDisabled = !SettingsService.DolphinPathsSetupCorrectly();
        DisabledWarningText.IsVisible = _settingsAreDisabled;

        // Recomp can be enabled with only a game image configured. Disable the Dolphin-only
        // controls individually so the recomp switch never becomes trapped behind Dolphin setup.
        LaunchRrOnStartup.IsEnabled = !_settingsAreDisabled;
        DolphinReinstallButton.IsEnabled = !_settingsAreDisabled;
        OpenGameFolderButton.IsEnabled = !_settingsAreDisabled && Directory.Exists(PathManager.RiivolutionWhWzFolderPath);
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
        OpenGameFolderButton.IsEnabled = Directory.Exists(PathManager.RiivolutionWhWzFolderPath);
        OpenSaveFolderButton.IsEnabled = Directory.Exists(PathManager.SaveFolderPath);
    }

    private void ForceLoadSettings()
    {
        // Always loads

        // The recomp only ships for Windows, so on every other platform the whole section stays hidden.
        var recompSupported = OperatingSystem.IsWindows();
        RecompSectionLabel.IsVisible = recompSupported;
        RecompBorder.IsVisible = recompSupported;
        RecompVrBorder.IsVisible = recompSupported;
        if (recompSupported)
            RefreshModes();
    }

    private void RefreshRetroRewindVersion()
    {
        var version = CustomDistributionSingletonService.RetroRewind.GetCurrentVersion()?.ToString() ?? t("state.unknown");
        RetroRewindVersionText.Text = t("helper_text.installed_version", version);
    }

    private void ClickLaunchRrOnStartup(object? sender, RoutedEventArgs e)
    {
        SettingsService.Set(SettingsService.LAUNCH_RR_ON_STARTUP, LaunchRrOnStartup.IsChecked == true);
    }

    private void ClickEnableRecomp(object? sender, RoutedEventArgs e)
    {
        if (_loadingModes)
            return;
        try
        {
            var vr = ReferenceEquals(sender, EnableRecompVR);
            SettingsService.Set(
                vr ? SettingsService.ENABLE_RECOMP_VR : SettingsService.ENABLE_RECOMP,
                vr ? EnableRecompVR.IsChecked == true : EnableRecomp.IsChecked == true
            );
        }
        catch (IOException)
        {
            _ = new MessageBoxWindow().SetTitleText("WiiCompiled is busy").SetInfoText(RecompOperationCoordinator.BusyMessage).ShowDialog();
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

    private async void Reinstall_RetroRewind(object sender, RoutedEventArgs e)
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
            return;
        }
        using var lease = operation;
        var progressWindow = new ProgressWindow();
        progressWindow.Show();
        try
        {
            var result = await CustomDistributionSingletonService.RetroRewind.ReinstallAsync(progressWindow);
            if (result.IsFailure)
                await new MessageBoxWindow()
                    .SetTitleText("Retro Rewind installation failed")
                    .SetInfoText(result.Error.Message)
                    .ShowDialog();
        }
        finally
        {
            progressWindow.Close();
            RefreshRetroRewindVersion();
        }
    }

    private void OpenSaveFolder_OnClick(object? sender, RoutedEventArgs e)
    {
        FilePickerHelper.OpenFolderInFileManager(PathManager.SaveFolderPath);
    }

    private void GameFileFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (!Directory.Exists(PathManager.RiivolutionWhWzFolderPath))
            return;

        FilePickerHelper.OpenFolderInFileManager(PathManager.RiivolutionWhWzFolderPath);
    }
}
