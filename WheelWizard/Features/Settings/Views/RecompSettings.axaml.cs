using System.IO.Abstractions;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using WheelWizard.ApplicationData;
using WheelWizard.Recomp;
using WheelWizard.Recomp.Domain;
using WheelWizard.Settings;
using WheelWizard.Settings.Types;
using WheelWizard.Shared.Desktop.Storage;
using WheelWizard.Shared.MessageTranslations;
using WheelWizard.Views.Components;
using WheelWizard.Views.Dialogs;
using WheelWizard.Views.Shell;

namespace WheelWizard.Settings.Views;

public partial class RecompSettings : UserControl
{
    private readonly IFileSystem fileSystem;

    private bool _loading;

    private IFilePickerService FilePicker { get; }

    private ISettingsManager SettingsService { get; }

    private IRecompSettingManager RecompSettingsFile { get; }

    private IRecompDolphinDataService? DolphinData { get; }

    private IRecompEnvironment? RecompEnvironment { get; }

    private IRecompInstallService? RecompInstallService { get; }

    private IRecompPaths RecompPaths { get; }

    private IApplicationDataLocation ApplicationData { get; }

    // The settings file of the backend in use: the VR installation keeps its own Config.toml.
    private RecompBackendSelection BackendSelection { get; }

    public RecompSettings(
        IFileSystem fileSystem,
        IFilePickerService filePicker,
        ISettingsManager settingsService,
        IRecompSettingManager recompSettingsFile,
        IRecompPaths recompPaths,
        IApplicationDataLocation applicationData,
        RecompBackendSelection backendSelection,
        IRecompDolphinDataService? dolphinData = null,
        IRecompEnvironment? recompEnvironment = null,
        IRecompInstallService? recompInstallService = null
    )
    {
        this.fileSystem = fileSystem;
        FilePicker = filePicker;
        SettingsService = settingsService;
        RecompSettingsFile = recompSettingsFile;
        RecompPaths = recompPaths;
        ApplicationData = applicationData;
        BackendSelection = backendSelection;
        DolphinData = dolphinData;
        RecompEnvironment = recompEnvironment;
        RecompInstallService = recompInstallService;
        InitializeComponent();

        // Config.toml is also written by the in-game settings bar, so opening the page rereads the
        // file rather than trusting whatever was loaded at startup.
        RecompSettingsFile.ReloadSettings(BackendSelection.ConfigPath);
        LoadSettings();

        // Attached after loading, so populating a control never writes it straight back.
        ShareDolphinData.IsCheckedChanged += ShareDolphinData_OnChanged;
        ResolutionDropdown.SelectionChanged += Resolution_OnChanged;
        GraphicsApiDropdown.SelectionChanged += GraphicsApi_OnChanged;
        ShowFps.IsCheckedChanged += ShowFps_OnChanged;
        PreventStutters.IsCheckedChanged += PreventStutters_OnChanged;
        VrEnabled.IsCheckedChanged += (_, _) =>
        {
            SaveVrSetting(SettingsService.RECOMP_VR_ENABLED, VrEnabled.IsChecked == true);
            RefreshGraphicsApiAvailability();
        };
        VrRaceViewDropdown.SelectionChanged += (_, _) =>
        {
            // One setting in two keys, as runtime_config.h's VrRaceView reads them.
            var view = VrRaceViewDropdown.SelectedIndex;
            if (view >= 0)
                SaveVrSettings(
                    () => SettingsService.Set(SettingsService.RECOMP_VR_FLAT_SCREEN, view == FlatScreenRaceView),
                    () => SettingsService.Set(SettingsService.RECOMP_VR_IMMERSIVE_WINDOW, view == ImmersiveWindowRaceView)
                );
        };
        VrRaceIntroDropdown.SelectionChanged += (_, _) =>
        {
            if (VrRaceIntroDropdown.SelectedIndex >= 0)
                SaveVrSetting(SettingsService.RECOMP_VR_FLAT_RACE_INTRO, VrRaceIntroDropdown.SelectedIndex == 1);
        };
        VrMirrorDropdown.SelectionChanged += (_, _) => SaveVrChoice(SettingsService.RECOMP_VR_MIRROR_VIEW, VrMirrorDropdown, MirrorValues);
        VrRenderScaleDropdown.SelectionChanged += (_, _) =>
        {
            var index = VrRenderScaleDropdown.SelectedIndex;
            if (index >= 0 && index < _renderScales.Count)
                SaveVrSetting(SettingsService.RECOMP_VR_RENDER_SCALE, _renderScales[index]);
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
            return RecompOperationCoordinator.Acquire(ApplicationData.DirectoryPath);
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

            var installFolder = RecompEnvironment?.InstallFolderPath ?? RecompPaths.InstallFolderPath;
            InstallLocationText.Text = installFolder;
            OpenInstallFolder.IsEnabled = installed && fileSystem.Directory.Exists(installFolder);
            UninstallButton.IsEnabled = installed;
            WiiCompiledVersionText.Text = t(
                "helper_text.installed_version",
                new { version = installed ? t("state.loading") : t("state.unknown") }
            );
            if (installed)
                _ = RefreshWiiCompiledVersionAsync();

            // VR first: whether OpenXR is on decides whether the graphics API can be chosen.
            LoadVrSettings();
            LoadVideoSettings();

            var sharingDolphinData = DolphinData is { IsSharingEnabled: true, SourceNandFolderPath: not null };
            ShareDolphinData.IsChecked = sharingDolphinData;
            SharedNandWarningIcon.IsVisible = sharingDolphinData;

            var cloneFolder = RecompPaths.NandCopyFolderPath;
            DolphinCloneStatus.Text = fileSystem.Directory.Exists(cloneFolder)
                ? t("status.recomp_dolphin_clone_available", new { path = cloneFolder })
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
        WiiCompiledVersionText.Text = t("helper_text.installed_version", new { version = version ?? t("state.unknown") });
    }

    // The scales the render-scale list currently offers, which is the fixed set plus whatever the
    // installation already holds when that is not one of them.
    private IReadOnlyList<double> _renderScales = RecompVideoConfig.RenderScales;

    private static readonly string[] MirrorValues = ["normal", "both", "left", "right", "none"];

    // Race view choices, in the order the dropdown lists them.
    private const int ImmersiveWindowRaceView = 1;
    private const int FlatScreenRaceView = 2;

    // The race camera rows live on their own tab, RecompCameraSettings.
    private void LoadVrSettings()
    {
        VrSection.IsVisible = RecompEnvironment?.Backend.Kind == RecompBackendKind.OpenXR;
        VrSection.IsEnabled = IsInstalled;
        if (!VrSection.IsVisible)
            return;
        VrEnabled.IsChecked = SettingsService.Get<bool>(SettingsService.RECOMP_VR_ENABLED);
        VrRaceViewDropdown.ItemsSource = new[] { "Immersive", "Immersive window", "Flat screen" };
        // Flat Screen mode wins over the immersive window, as in runtime_config.h's VrRaceView.
        VrRaceViewDropdown.SelectedIndex =
            SettingsService.Get<bool>(SettingsService.RECOMP_VR_FLAT_SCREEN) ? FlatScreenRaceView
            : SettingsService.Get<bool>(SettingsService.RECOMP_VR_IMMERSIVE_WINDOW) ? ImmersiveWindowRaceView
            : 0;
        VrRaceIntroDropdown.ItemsSource = new[] { "Immersive", "Flat screen" };
        VrRaceIntroDropdown.SelectedIndex = SettingsService.Get<bool>(SettingsService.RECOMP_VR_FLAT_RACE_INTRO) ? 1 : 0;
        RefreshRaceIntroRow();
        VrMirrorDropdown.ItemsSource = new[] { "Normal game", "Both eyes", "Left eye", "Right eye", "Black screen" };
        VrMirrorDropdown.SelectedIndex = Array.IndexOf(MirrorValues, SettingsService.Get<string>(SettingsService.RECOMP_VR_MIRROR_VIEW));
        // A scale set outside WheelWizard (the in-game panel takes any number) joins the list rather
        // than being shown as the nearest offered one, so the row never misreports what the game uses.
        var scale = SettingsService.Get<double>(SettingsService.RECOMP_VR_RENDER_SCALE);
        _renderScales = RecompVideoConfig.RenderScalesIncluding(scale);
        VrRenderScaleDropdown.ItemsSource = _renderScales.Select(RecompVideoConfig.DescribeRenderScale).ToList();
        VrRenderScaleDropdown.SelectedIndex = RecompVideoConfig.FindClosestRenderScaleIndex(_renderScales, scale);
    }

    private void SaveVrChoice(Setting<string> setting, ComboBox dropdown, string[] values)
    {
        if (dropdown.SelectedIndex >= 0 && dropdown.SelectedIndex < values.Length)
            SaveVrSetting(setting, values[dropdown.SelectedIndex]);
    }

    // Flat Screen mode keeps the whole race on the menu screen, its intro included.
    private void RefreshRaceIntroRow() => VrRaceIntroRow.IsEnabled = VrRaceViewDropdown.SelectedIndex != FlatScreenRaceView;

    private void SaveVrSetting<T>(Setting<T> setting, T value) => SaveVrSettings(() => SettingsService.Set(setting, value));

    private void SaveVrSettings(params Func<bool>[] writes)
    {
        if (_loading || RecompEnvironment?.Backend.Kind != RecompBackendKind.OpenXR || !IsInstalled)
            return;
        using var operation = TryAcquireSharedData();
        if (operation is null)
        {
            RecompSettingsFile.ReloadSettings(BackendSelection.ConfigPath);
            LoadSettings();
            return;
        }
        foreach (var write in writes)
            write();
        RefreshRaceIntroRow();
    }

    /// <summary>
    /// Every VR and Camera setting back to its default, as the in-game panel's Reset VR settings does:
    /// the keys leave Config.toml, so the game reads its own defaults, and the rows read them back.
    /// </summary>
    private async void ResetVrSettings_OnClick(object? sender, RoutedEventArgs e)
    {
        if (RecompEnvironment?.Backend.Kind != RecompBackendKind.OpenXR || !IsInstalled)
            return;
        var confirmed = await new YesNoWindow()
            .SetMainText("Reset VR settings?")
            .SetExtraText(
                "Every VR and Camera setting goes back to its default, those only the in-game settings show included. Enable OpenXR keeps its choice."
            )
            .SetButtonText("Reset", t("action.cancel"))
            .SetButtonTones(ActionButtonTone.Danger, ActionButtonTone.Secondary)
            .AwaitAnswer();
        if (!confirmed)
            return;
        using (var operation = TryAcquireSharedData())
        {
            if (operation is null)
                return;
            RecompSettingsFile.RemoveTomlSettings(BackendSelection.ConfigPath, "vr", RecompVrConfig.PreferenceKeys);
        }
        RecompSettingsFile.ReloadSettings(BackendSelection.ConfigPath);
        LoadSettings();
        ViewUtils.ShowSnackbar("VR settings reset to their defaults.");
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
        RefreshGraphicsApiAvailability();

        ShowFps.IsChecked = SettingsService.Get<bool>(SettingsService.RECOMP_SHOW_FPS);
        PreventStutters.IsChecked = SettingsService.Get<bool>(SettingsService.RECOMP_PREVENT_STUTTERS);
    }

    /// <summary>
    /// Both offered APIs drive the headset, so the choice stays open while OpenXR is on. Only a value
    /// from outside that pair is refused, and a launch repairs it to DirectX 12; the row is preselected
    /// so it says what the game will actually use. The note carries what the choice costs in VR.
    /// </summary>
    private void RefreshGraphicsApiAvailability()
    {
        var vrBackend = RecompEnvironment?.Backend.Kind == RecompBackendKind.OpenXR;
        var vrOn = vrBackend && VrEnabled.IsChecked == true;
        GraphicsApiVrNote.IsVisible = vrOn;
        if (vrOn && GraphicsApiDropdown.SelectedIndex < 0)
            GraphicsApiDropdown.SelectedIndex = RecompVideoConfig.OfferedGraphicsApis.ToList().IndexOf("d3d12");
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

        SettingsEditing.Set(SettingsService, SettingsService.RECOMP_RESOLUTION_MULTIPLIER, RecompVideoConfig.ResolutionMultipliers[index]);
    }

    private void GraphicsApi_OnChanged(object? sender, SelectionChangedEventArgs e)
    {
        var index = GraphicsApiDropdown.SelectedIndex;
        if (_loading || index < 0 || index >= RecompVideoConfig.OfferedGraphicsApis.Count)
            return;

        SettingsEditing.Set(SettingsService, SettingsService.RECOMP_GRAPHICS_API, RecompVideoConfig.OfferedGraphicsApis[index]);
    }

    private void ShowFps_OnChanged(object? sender, RoutedEventArgs e)
    {
        if (_loading)
            return;

        SettingsEditing.Set(SettingsService, SettingsService.RECOMP_SHOW_FPS, ShowFps.IsChecked == true);
    }

    private void PreventStutters_OnChanged(object? sender, RoutedEventArgs e)
    {
        if (_loading)
            return;

        SettingsEditing.Set(SettingsService, SettingsService.RECOMP_PREVENT_STUTTERS, PreventStutters.IsChecked == true);
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
            .SetButtonTones(WheelWizard.Views.Components.ActionButtonTone.Primary, WheelWizard.Views.Components.ActionButtonTone.Secondary)
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

        var cloneFolder = RecompPaths.NandCopyFolderPath;
        if (fileSystem.Directory.Exists(cloneFolder))
        {
            var overwrite = await new YesNoWindow()
                .SetMainText(t("question.recomp_overwrite_dolphin_clone.title"))
                .SetExtraText(t("question.recomp_overwrite_dolphin_clone.extra"))
                .SetButtonText(t("action.clone"), t("action.cancel"))
                .SetButtonTones(
                    WheelWizard.Views.Components.ActionButtonTone.Primary,
                    WheelWizard.Views.Components.ActionButtonTone.Secondary
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
        var installFolder = RecompEnvironment?.InstallFolderPath ?? RecompPaths.InstallFolderPath;
        if (fileSystem.Directory.Exists(installFolder))
            FilePicker.OpenFolderInFileManager(installFolder);
    }

    private async void Uninstall_OnClick(object? sender, RoutedEventArgs e)
    {
        if (RecompInstallService is null)
            return;

        // Only this backend's compiled installation and download cache go; every NAND and the
        // configuration stay, since both backends share them.
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
