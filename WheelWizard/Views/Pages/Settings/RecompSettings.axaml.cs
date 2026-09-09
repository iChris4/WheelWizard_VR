using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using WheelWizard.Recomp;
using WheelWizard.Services;
using WheelWizard.Settings;
using WheelWizard.Settings.Types;
using WheelWizard.Shared.DependencyInjection;
using WheelWizard.Shared.MessageTranslations;
using WheelWizard.Views.Popups.Generic;

namespace WheelWizard.Views.Pages.Settings;

public partial class RecompSettings : UserControlBase
{
    private bool _loading;

    [Inject]
    private ISettingsManager SettingsService { get; set; } = null!;

    [Inject]
    private IRecompSettingManager RecompSettingsFile { get; set; } = null!;

    [Inject]
    private IRecompDolphinDataService? DolphinData { get; set; }

    [Inject]
    private IRecompEnvironment? RecompEnvironment { get; set; }

    [Inject]
    private IRecompInstallService? RecompInstallService { get; set; }

    public RecompSettings()
    {
        InitializeComponent();

        // Config.toml is also written by the in-game settings bar, so opening the page rereads the
        // file rather than trusting whatever was loaded at startup.
        RecompSettingsFile.ReloadSettings();
        LoadSettings();

        // Attached after loading, so populating a control never writes it straight back.
        ShareDolphinData.IsCheckedChanged += ShareDolphinData_OnChanged;
        ResolutionDropdown.SelectionChanged += Resolution_OnChanged;
        GraphicsApiDropdown.SelectionChanged += GraphicsApi_OnChanged;
        ShowFps.IsCheckedChanged += ShowFps_OnChanged;
        PreventStutters.IsCheckedChanged += PreventStutters_OnChanged;
        VrEnabled.IsCheckedChanged += (_, _) => SaveVrSetting(SettingsService.RECOMP_VR_ENABLED, VrEnabled.IsChecked == true);
        VrHideDriver.IsCheckedChanged += (_, _) => SaveVrSetting(SettingsService.RECOMP_VR_HIDE_DRIVER, VrHideDriver.IsChecked == true);
        VrMirrorDropdown.SelectionChanged += (_, _) => SaveVrChoice(SettingsService.RECOMP_VR_MIRROR_VIEW, VrMirrorDropdown, MirrorValues);
        VrCameraDropdown.SelectionChanged += (_, _) =>
        {
            if (VrCameraDropdown.SelectedIndex >= 0)
                SaveVrSetting(SettingsService.RECOMP_VR_FIRST_PERSON, VrCameraDropdown.SelectedIndex == 1);
        };
        VrRotationDropdown.SelectionChanged += (_, _) =>
            SaveVrChoice(SettingsService.RECOMP_VR_FIRST_PERSON_ROTATION, VrRotationDropdown, RotationValues);
        VrRenderScale.ValueChanged += (_, _) =>
        {
            if (VrRenderScale.Value is { } value)
                SaveVrSetting(SettingsService.RECOMP_VR_RENDER_SCALE, (double)value);
        };
        AttachedToVisualTree += (_, _) => RecompOperationCoordinator.Changed += RefreshOperationState;
        DetachedFromVisualTree += (_, _) => RecompOperationCoordinator.Changed -= RefreshOperationState;
        RefreshOperationState();
    }

    private bool IsInstalled => RecompInstallService is { IsInstalled: true };

    private void RefreshOperationState() => Dispatcher.UIThread.Post(() => IsEnabled = !RecompOperationCoordinator.IsBusy);

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

    private void LoadSettings()
    {
        _loading = true;
        try
        {
            var installed = IsInstalled;
            NotInstalledText.IsVisible = !installed;
            VideoBorder.IsEnabled = installed;
            InstallationBorder.IsEnabled = installed;

            var installFolder = RecompEnvironment?.InstallFolderPath ?? PathManager.RecompInstallFolderPath;
            InstallLocationText.Text = installFolder;
            OpenInstallFolder.IsEnabled = installed && Directory.Exists(installFolder);
            UninstallButton.IsEnabled = installed;
            WiiCompiledVersionText.Text = t("helper_text.installed_version", installed ? t("state.loading") : t("state.unknown"));
            if (installed)
                _ = RefreshWiiCompiledVersionAsync();

            LoadVideoSettings();
            LoadVrSettings();

            var sharingDolphinData = DolphinData is { IsSharingEnabled: true, SourceNandFolderPath: not null };
            ShareDolphinData.IsChecked = sharingDolphinData;
            SharedNandWarningIcon.IsVisible = sharingDolphinData;

            var cloneFolder = PathManager.RecompNandCopyFolderPath;
            DolphinCloneStatus.Text = Directory.Exists(cloneFolder)
                ? t("status.recomp_dolphin_clone_available", cloneFolder)
                : t("status.recomp_dolphin_clone_missing");
        }
        finally
        {
            _loading = false;
        }
    }

    private async Task RefreshWiiCompiledVersionAsync()
    {
        var version = RecompInstallService is null ? null : await RecompInstallService.GetInstalledVersionAsync();
        if (!IsInstalled)
            return;
        WiiCompiledVersionText.Text = t("helper_text.installed_version", version ?? t("state.unknown"));
    }

    private static readonly string[] MirrorValues = ["normal", "both", "left", "right", "none"];
    private static readonly string[] RotationValues = ["yaw", "yaw_pitch", "full"];

    private void LoadVrSettings()
    {
        VrSection.IsVisible = RecompEnvironment?.Backend.Kind == RecompBackendKind.OpenXR;
        VrSection.IsEnabled = IsInstalled;
        if (!VrSection.IsVisible)
            return;
        VrEnabled.IsChecked = SettingsService.Get<bool>(SettingsService.RECOMP_VR_ENABLED);
        VrHideDriver.IsChecked = SettingsService.Get<bool>(SettingsService.RECOMP_VR_HIDE_DRIVER);
        VrMirrorDropdown.ItemsSource = new[] { "Normal game", "Both eyes", "Left eye", "Right eye", "Black screen" };
        VrMirrorDropdown.SelectedIndex = Array.IndexOf(MirrorValues, SettingsService.Get<string>(SettingsService.RECOMP_VR_MIRROR_VIEW));
        VrCameraDropdown.ItemsSource = new[] { "Chase camera", "First person" };
        VrCameraDropdown.SelectedIndex = SettingsService.Get<bool>(SettingsService.RECOMP_VR_FIRST_PERSON) ? 1 : 0;
        VrRotationDropdown.ItemsSource = new[] { "Turning only", "Turning + pitch", "Full rotation" };
        VrRotationDropdown.SelectedIndex = Array.IndexOf(
            RotationValues,
            SettingsService.Get<string>(SettingsService.RECOMP_VR_FIRST_PERSON_ROTATION)
        );
        var scale = SettingsService.Get<double>(SettingsService.RECOMP_VR_RENDER_SCALE);
        VrRenderScale.Value = double.IsFinite(scale) ? (decimal)Math.Clamp(scale, 0.25, 2.0) : 1m;
        RefreshVrCameraControls();
    }

    private void RefreshVrCameraControls()
    {
        var firstPerson = VrCameraDropdown.SelectedIndex == 1;
        VrRotationDropdown.IsEnabled = firstPerson;
        VrHideDriver.IsEnabled = firstPerson;
    }

    private void SaveVrChoice(Setting setting, ComboBox dropdown, string[] values)
    {
        if (dropdown.SelectedIndex >= 0 && dropdown.SelectedIndex < values.Length)
            SaveVrSetting(setting, values[dropdown.SelectedIndex]);
    }

    private void SaveVrSetting(Setting setting, object value)
    {
        if (_loading || RecompEnvironment?.Backend.Kind != RecompBackendKind.OpenXR || !IsInstalled)
            return;
        using var operation = TryAcquireSharedData();
        if (operation is null)
        {
            RecompSettingsFile.ReloadSettings();
            LoadSettings();
            return;
        }
        SettingsService.Set(setting, value);
        RefreshVrCameraControls();
    }

    #region WiiCompiled video settings

    private void LoadVideoSettings()
    {
        ResolutionDropdown.Items.Clear();
        foreach (var multiplier in RecompVideoConfig.ResolutionMultipliers)
        {
            ResolutionDropdown.Items.Add(RecompVideoConfig.DescribeResolution(multiplier));
        }

        ResolutionDropdown.SelectedIndex = FindClosestResolutionIndex(
            SettingsService.Get<double>(SettingsService.RECOMP_RESOLUTION_MULTIPLIER)
        );

        GraphicsApiDropdown.Items.Clear();
        foreach (var api in RecompVideoConfig.OfferedGraphicsApis)
        {
            GraphicsApiDropdown.Items.Add(RecompVideoConfig.DescribeGraphicsApi(api));
        }

        // A value outside the offered list (such as the backend default "auto") simply shows no
        // selection; it is replaced the moment the user picks a real one.
        var currentApi = SettingsService.Get<string>(SettingsService.RECOMP_GRAPHICS_API);
        GraphicsApiDropdown.SelectedIndex = RecompVideoConfig.OfferedGraphicsApis.ToList().IndexOf(currentApi);
        GraphicsApiDropdown.IsEnabled = RecompEnvironment?.Backend.Kind != RecompBackendKind.OpenXR;

        ShowFps.IsChecked = SettingsService.Get<bool>(SettingsService.RECOMP_SHOW_FPS);
        PreventStutters.IsChecked = SettingsService.Get<bool>(SettingsService.RECOMP_PREVENT_STUTTERS);
    }

    /// <summary>
    /// Maps whatever multiplier the recomp currently holds onto the nearest offered option, so a value
    /// set outside Wheel Wizard is shown as the closest thing rather than as an empty dropdown.
    /// </summary>
    private static int FindClosestResolutionIndex(double multiplier)
    {
        var multipliers = RecompVideoConfig.ResolutionMultipliers;
        var bestIndex = 0;
        var bestDistance = double.MaxValue;
        for (var index = 0; index < multipliers.Count; index++)
        {
            var distance = Math.Abs(multipliers[index] - multiplier);
            if (distance >= bestDistance)
                continue;

            bestDistance = distance;
            bestIndex = index;
        }

        return bestIndex;
    }

    private void Resolution_OnChanged(object? sender, SelectionChangedEventArgs e)
    {
        var index = ResolutionDropdown.SelectedIndex;
        if (_loading || index < 0 || index >= RecompVideoConfig.ResolutionMultipliers.Count)
            return;

        SettingsService.Set(SettingsService.RECOMP_RESOLUTION_MULTIPLIER, RecompVideoConfig.ResolutionMultipliers[index]);
    }

    private void GraphicsApi_OnChanged(object? sender, SelectionChangedEventArgs e)
    {
        var index = GraphicsApiDropdown.SelectedIndex;
        if (_loading || index < 0 || index >= RecompVideoConfig.OfferedGraphicsApis.Count)
            return;

        SettingsService.Set(SettingsService.RECOMP_GRAPHICS_API, RecompVideoConfig.OfferedGraphicsApis[index]);
    }

    private void ShowFps_OnChanged(object? sender, RoutedEventArgs e)
    {
        if (_loading)
            return;

        SettingsService.Set(SettingsService.RECOMP_SHOW_FPS, ShowFps.IsChecked == true);
    }

    private void PreventStutters_OnChanged(object? sender, RoutedEventArgs e)
    {
        if (_loading)
            return;

        SettingsService.Set(SettingsService.RECOMP_PREVENT_STUTTERS, PreventStutters.IsChecked == true);
    }

    #endregion

    private async void ShareDolphinData_OnChanged(object? sender, RoutedEventArgs e)
    {
        if (_loading || DolphinData is null)
            return;
        using var operation = TryAcquireSharedData();
        if (operation is null)
        {
            LoadSettings();
            return;
        }

        if (ShareDolphinData.IsChecked != true)
        {
            DolphinData.SetSharingEnabled(false);
            await ApplyNandSettingAsync();
            LoadSettings();
            return;
        }

        var userFolder = DolphinData.LinkedUserFolderPath ?? await Task.Run(DolphinData.FindCandidateUserFolder);
        if (userFolder is null)
        {
            DolphinData.SetSharingEnabled(false);
            await ApplyNandSettingAsync();
            LoadSettings();
            await new MessageBoxWindow()
                .SetMessageType(MessageBoxWindow.MessageType.Warning)
                .SetTitleText(t("status.recomp_dolphin_data_not_found"))
                .SetInfoText(t("helper_text.recomp_dolphin_data_not_found"))
                .ShowDialog();
            return;
        }

        var confirmed = await new YesNoWindow()
            .SetMainText(t("question.recomp_share_dolphin_data.title"))
            .SetExtraText(t("question.recomp_share_dolphin_data.extra"))
            .SetButtonText(t("action.recomp_nand_share"), t("action.cancel"))
            .SetButtonVariants(
                WheelWizard.Views.Components.Button.ButtonsVariantType.Warning,
                WheelWizard.Views.Components.Button.ButtonsVariantType.Default
            )
            .AwaitAnswer();
        if (!confirmed)
        {
            LoadSettings();
            return;
        }

        var result = DolphinData.Link(userFolder);
        if (result.IsFailure)
        {
            DolphinData.SetSharingEnabled(false);
            await ApplyNandSettingAsync();
            await new MessageBoxWindow()
                .SetMessageType(MessageBoxWindow.MessageType.Error)
                .SetTitleText(t("status.recomp_dolphin_data_not_found"))
                .SetInfoText(result.Error.Message)
                .ShowDialog();
        }
        else
        {
            await ApplyNandSettingAsync();
        }
        LoadSettings();
    }

    private async void CloneDolphinData_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DolphinData is null)
            return;
        using var operation = TryAcquireSharedData();
        if (operation is null)
            return;

        var sourceNand = await Task.Run(() => DolphinData.SourceNandFolderPath);
        if (sourceNand is null)
        {
            await new MessageBoxWindow()
                .SetMessageType(MessageBoxWindow.MessageType.Warning)
                .SetTitleText(t("status.recomp_dolphin_data_not_found"))
                .SetInfoText(t("helper_text.recomp_dolphin_data_not_found"))
                .ShowDialog();
            return;
        }

        var cloneFolder = PathManager.RecompNandCopyFolderPath;
        if (Directory.Exists(cloneFolder))
        {
            var overwrite = await new YesNoWindow()
                .SetMainText(t("question.recomp_overwrite_dolphin_clone.title"))
                .SetExtraText(t("question.recomp_overwrite_dolphin_clone.extra"))
                .SetButtonText(t("action.clone"), t("action.cancel"))
                .SetButtonVariants(
                    WheelWizard.Views.Components.Button.ButtonsVariantType.Warning,
                    WheelWizard.Views.Components.Button.ButtonsVariantType.Default
                )
                .AwaitAnswer();
            if (!overwrite)
                return;
        }

        var progressText = t("progress.recomp_copying_nand");
        var progressWindow = new ProgressWindow(progressText).SetGoal(progressText).SetIndeterminate();
        IsEnabled = false;
        progressWindow.Show();
        try
        {
            var copyResult = await Task.Run(DolphinData.CopyNandForRecomp);
            if (copyResult.IsFailure)
            {
                MessageTranslationHelper.ShowMessage(copyResult.Error);
                return;
            }

            // A clone is private data, so completing one also leaves direct sharing mode.
            DolphinData.SetCopyEnabled(true);
            DolphinData.SetSharingEnabled(false);
            await ApplyNandSettingAsync();
            ViewUtils.ShowSnackbar(t("status.recomp_dolphin_data_cloned"));
        }
        finally
        {
            progressWindow.Close();
            IsEnabled = true;
            LoadSettings();
        }
    }

    /// <summary>
    /// The runtime reads <c>paths.nand_root</c> from its Config.toml at launch, so a changed sharing
    /// choice takes effect on the next launch without reinstalling anything.
    /// </summary>
    private async Task ApplyNandSettingAsync()
    {
        var dolphinData = DolphinData;
        if (dolphinData is null)
            return;

        var result = await Task.Run(dolphinData.ApplyNandToRecompConfig);
        if (result.IsFailure)
            MessageTranslationHelper.ShowMessage(result.Error);
    }

    private void OpenInstallFolder_OnClick(object? sender, RoutedEventArgs e)
    {
        var installFolder = RecompEnvironment?.InstallFolderPath ?? PathManager.RecompInstallFolderPath;
        if (Directory.Exists(installFolder))
            FilePickerHelper.OpenFolderInFileManager(installFolder);
    }

    private async void Uninstall_OnClick(object? sender, RoutedEventArgs e)
    {
        if (RecompInstallService is null)
            return;

        // Shared Dolphin data lives in Dolphin's own Wii folder and survives; a copied or private
        // NAND belongs to the recomp and is removed with it, which the user must know upfront.
        var extraText =
            "Remove this backend's compiled installation and download cache? Saves, Miis, controller settings and Retro Rewind content will be kept.";
        var confirmed = await new YesNoWindow()
            .SetMainText(t("question.recomp_uninstall.title"))
            .SetExtraText(extraText)
            .SetButtonText(t("action.un_install"), t("action.cancel"))
            .AwaitAnswer();
        if (!confirmed)
            return;

        var progressText = t("progress.uninstalling_recomp");
        var progressWindow = new ProgressWindow(progressText).SetGoal(progressText).SetIndeterminate();

        IsEnabled = false;
        progressWindow.Show();
        try
        {
            var result = await RecompInstallService.UninstallAsync();
            if (result.IsFailure)
            {
                MessageTranslationHelper.ShowMessage(result.Error);
                return;
            }

            ViewUtils.ShowSnackbar(t("status.recomp_uninstalled"));
        }
        finally
        {
            progressWindow.Close();
            IsEnabled = true;
            LoadSettings();
        }
    }
}
