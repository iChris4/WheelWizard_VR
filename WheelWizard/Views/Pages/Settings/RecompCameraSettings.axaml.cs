using Avalonia.Controls;
using Avalonia.Threading;
using WheelWizard.Recomp;
using WheelWizard.Settings;
using WheelWizard.Settings.Types;
using WheelWizard.Shared.DependencyInjection;
using WheelWizard.Views.Popups.Generic;

namespace WheelWizard.Views.Pages.Settings;

/// <summary>
/// The OpenXR VR race camera, on its own tab as in the game's own settings panel. Like the OpenXR VR
/// tab, it writes the VR installation's Config.toml, and only once that installation exists.
/// </summary>
public partial class RecompCameraSettings : UserControlBase
{
    private bool _loading;

    [Inject]
    private ISettingsManager SettingsService { get; set; } = null!;

    [Inject]
    private IRecompSettingManager RecompSettingsFile { get; set; } = null!;

    [Inject]
    private IRecompEnvironment? RecompEnvironment { get; set; }

    [Inject]
    private IRecompInstallService? RecompInstallService { get; set; }

    private static readonly string[] RotationValues = ["yaw", "yaw_pitch", "full"];

    public RecompCameraSettings()
    {
        InitializeComponent();

        // Config.toml is also written by the in-game settings bar, so opening the page rereads the
        // file rather than trusting whatever was loaded at startup.
        RecompSettingsFile.ReloadSettings();
        LoadSettings();

        // Attached after loading, so populating a control never writes it straight back.
        VrCameraDropdown.SelectionChanged += (_, _) =>
        {
            if (VrCameraDropdown.SelectedIndex >= 0)
                SaveVrSetting(SettingsService.RECOMP_VR_FIRST_PERSON, VrCameraDropdown.SelectedIndex == 1);
        };
        VrRotationDropdown.SelectionChanged += (_, _) =>
        {
            var index = VrRotationDropdown.SelectedIndex;
            if (index >= 0 && index < RotationValues.Length)
                SaveVrSetting(SettingsService.RECOMP_VR_FIRST_PERSON_ROTATION, RotationValues[index]);
        };
        VrHideDriver.IsCheckedChanged += (_, _) => SaveVrSetting(SettingsService.RECOMP_VR_HIDE_DRIVER, VrHideDriver.IsChecked == true);
        VrHandSteering.IsCheckedChanged += (_, _) =>
            SaveVrSetting(SettingsService.RECOMP_VR_HAND_STEERING, VrHandSteering.IsChecked == true);
        AttachedToVisualTree += (_, _) => RecompOperationCoordinator.Changed += RefreshOperationState;
        DetachedFromVisualTree += (_, _) => RecompOperationCoordinator.Changed -= RefreshOperationState;
        RefreshOperationState();
    }

    private bool IsInstalled => RecompInstallService is { IsInstalled: true };

    private bool IsVrBackend => RecompEnvironment?.Backend.Kind == RecompBackendKind.OpenXR;

    private void RefreshOperationState() => Dispatcher.UIThread.Post(() => IsEnabled = !RecompOperationCoordinator.IsBusy);

    private void LoadSettings()
    {
        _loading = true;
        try
        {
            CameraSection.IsEnabled = IsVrBackend && IsInstalled;
            VrCameraDropdown.ItemsSource = new[] { "Chase camera", "First person" };
            VrCameraDropdown.SelectedIndex = SettingsService.Get<bool>(SettingsService.RECOMP_VR_FIRST_PERSON) ? 1 : 0;
            VrRotationDropdown.ItemsSource = new[] { "Turning only", "Turning + pitch", "Full rotation" };
            VrRotationDropdown.SelectedIndex = Array.IndexOf(
                RotationValues,
                SettingsService.Get<string>(SettingsService.RECOMP_VR_FIRST_PERSON_ROTATION)
            );
            VrHideDriver.IsChecked = SettingsService.Get<bool>(SettingsService.RECOMP_VR_HIDE_DRIVER);
            VrHandSteering.IsChecked = SettingsService.Get<bool>(SettingsService.RECOMP_VR_HAND_STEERING);
            RefreshFirstPersonControls();
        }
        finally
        {
            _loading = false;
        }
    }

    private void RefreshFirstPersonControls()
    {
        var firstPerson = VrCameraDropdown.SelectedIndex == 1;
        VrRotationDropdown.IsEnabled = firstPerson;
        VrHideDriver.IsEnabled = firstPerson;
        VrHandSteering.IsEnabled = firstPerson;
    }

    private void SaveVrSetting(Setting setting, object value)
    {
        if (_loading || !IsVrBackend || !IsInstalled)
            return;
        using var operation = TryAcquireSharedData();
        if (operation is null)
        {
            RecompSettingsFile.ReloadSettings();
            LoadSettings();
            return;
        }
        SettingsService.Set(setting, value);
        RefreshFirstPersonControls();
    }

    private IDisposable? TryAcquireSharedData()
    {
        try
        {
            return RecompOperationCoordinator.Acquire();
        }
        catch (IOException)
        {
            _ = new MessageBoxWindow().SetTitleText("WiiCompiled is busy").SetInfoText(RecompOperationCoordinator.BusyMessage).ShowDialog();
            return null;
        }
    }
}
