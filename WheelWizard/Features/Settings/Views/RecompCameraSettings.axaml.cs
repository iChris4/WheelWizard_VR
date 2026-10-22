using System.Globalization;
using Avalonia.Controls;
using Avalonia.Threading;
using WheelWizard.ApplicationData;
using WheelWizard.Recomp;
using WheelWizard.Settings.Types;
using WheelWizard.Views.Components;
using WheelWizard.Views.Dialogs;

namespace WheelWizard.Settings.Views;

/// <summary>
/// The OpenXR VR race camera, on its own tab as in the game's own settings panel, with the rows of
/// the Quest launcher's Camera tab (tracked hands are Quest-only); the race view and its intro are on
/// the OpenXR VR tab, as there. Like that tab, it writes the VR installation's Config.toml, and only
/// once that installation exists. A row is greyed out while the choices above it, or the race view,
/// leave it nothing to do, as on the Quest.
/// </summary>
public partial class RecompCameraSettings : UserControl
{
    private enum Hiding
    {
        Nothing,
        Driver,
        DriverAndKart,
    }

    private sealed record SliderRow(ValueSlider Slider, TextBlock ValueText, Setting<double> Setting, string Format, int Decimals);

    private static readonly string[] RotationValues = ["yaw", "yaw_pitch", "full"];
    private static readonly string[] BulletBillValues = ["inside", "game"];
    private static readonly string[] SeatValues = ["cockpit", "custom"];
    private static readonly string[] ItemHandValues = ["left", "right", "off"];

    private bool _loading;
    private readonly List<SliderRow> _sliders;

    // A slider writes once it settles rather than at every step of a drag.
    private readonly DispatcherTimer _sliderSave = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly Dictionary<Setting<double>, double> _pendingSliders = [];

    private ISettingsManager SettingsService { get; }

    private IRecompSettingManager RecompSettingsFile { get; }

    private IApplicationDataLocation ApplicationData { get; }

    // The settings file of the backend in use: the VR installation keeps its own Config.toml.
    private RecompBackendSelection BackendSelection { get; }

    private IRecompEnvironment? RecompEnvironment { get; }

    private IRecompInstallService? RecompInstallService { get; }

    public RecompCameraSettings(
        ISettingsManager settingsService,
        IRecompSettingManager recompSettingsFile,
        IApplicationDataLocation applicationData,
        RecompBackendSelection backendSelection,
        IRecompEnvironment? recompEnvironment = null,
        IRecompInstallService? recompInstallService = null
    )
    {
        SettingsService = settingsService;
        RecompSettingsFile = recompSettingsFile;
        ApplicationData = applicationData;
        BackendSelection = backendSelection;
        RecompEnvironment = recompEnvironment;
        RecompInstallService = recompInstallService;
        InitializeComponent();
        _sliders =
        [
            new(LeanBackSlider, LeanBackValue, SettingsService.RECOMP_VR_LEAN_BACK_DEGREES, "{0:0}°", 0),
            new(LeftHandRollSlider, LeftHandRollValue, SettingsService.RECOMP_VR_BODY_IK_LEFT_HAND_ROLL, "{0:0}°", 0),
            new(RightHandRollSlider, RightHandRollValue, SettingsService.RECOMP_VR_BODY_IK_RIGHT_HAND_ROLL, "{0:0}°", 0),
            new(WheelKartSlider, WheelKartValue, SettingsService.RECOMP_VR_WHEEL_KART_DEGREES, "{0:0}°", 0),
            new(WheelBikeSlider, WheelBikeValue, SettingsService.RECOMP_VR_WHEEL_BIKE_DEGREES, "{0:0}°", 0),
            new(ItemSizeSlider, ItemSizeValue, SettingsService.RECOMP_VR_COCKPIT_ITEM_SIZE, "{0:0.00} m", 2),
        ];

        // Config.toml is also written by the in-game settings bar, so opening the page rereads the
        // file rather than trusting whatever was loaded at startup.
        RecompSettingsFile.ReloadSettings(BackendSelection.ConfigPath);
        LoadSettings();

        // Attached after loading, so populating a control never writes it straight back.
        DefaultCameraDropdown.SelectionChanged += (_, _) =>
        {
            if (DefaultCameraDropdown.SelectedIndex >= 0)
                Save(Write(SettingsService.RECOMP_VR_FIRST_PERSON, DefaultCameraDropdown.SelectedIndex == 1));
        };
        BulletBillDropdown.SelectionChanged += (_, _) =>
            SaveChoice(SettingsService.RECOMP_VR_BULLET_BILL_CAMERA, BulletBillDropdown, BulletBillValues);
        RotationDropdown.SelectionChanged += (_, _) =>
            SaveChoice(SettingsService.RECOMP_VR_FIRST_PERSON_ROTATION, RotationDropdown, RotationValues);
        HideDropdown.SelectionChanged += (_, _) => SaveHiding();
        SeatDropdown.SelectionChanged += (_, _) => SaveChoice(SettingsService.RECOMP_VR_FIRST_PERSON_SEAT, SeatDropdown, SeatValues);
        ItemHandDropdown.SelectionChanged += (_, _) =>
            SaveChoice(SettingsService.RECOMP_VR_COCKPIT_ITEM_HAND, ItemHandDropdown, ItemHandValues);
        BodyIkSwitch.IsCheckedChanged += (_, _) => Save(Write(SettingsService.RECOMP_VR_BODY_IK, BodyIkSwitch.IsChecked == true));
        BodyIkHandsOnlySwitch.IsCheckedChanged += (_, _) =>
            Save(Write(SettingsService.RECOMP_VR_BODY_IK_HANDS_ONLY, BodyIkHandsOnlySwitch.IsChecked == true));
        PlaceholderWheelSwitch.IsCheckedChanged += (_, _) =>
            Save(Write(SettingsService.RECOMP_VR_PLACEHOLDER_STEERING_WHEEL, PlaceholderWheelSwitch.IsChecked == true));
        HandSteeringSwitch.IsCheckedChanged += (_, _) =>
            Save(Write(SettingsService.RECOMP_VR_HAND_STEERING, HandSteeringSwitch.IsChecked == true));
        ItemThrowSwitch.IsCheckedChanged += (_, _) =>
            Save(Write(SettingsService.RECOMP_VR_COCKPIT_ITEM_THROW, ItemThrowSwitch.IsChecked == true));
        foreach (var row in _sliders)
            row.Slider.ValueChanged += (_, _) => SliderMoved(row);
        _sliderSave.Tick += (_, _) => SavePendingSliders();

        AttachedToVisualTree += (_, _) => RecompOperationCoordinator.Changed += RefreshOperationState;
        DetachedFromVisualTree += (_, _) =>
        {
            RecompOperationCoordinator.Changed -= RefreshOperationState;
            // Leaving the tab mid-settle still keeps the last slider position.
            SavePendingSliders();
        };
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
            DefaultCameraDropdown.ItemsSource = new[] { "Game camera", "First person" };
            DefaultCameraDropdown.SelectedIndex = SettingsService.Get(SettingsService.RECOMP_VR_FIRST_PERSON) ? 1 : 0;
            BulletBillDropdown.ItemsSource = new[] { "Ride inside", "Game camera" };
            BulletBillDropdown.SelectedIndex = ChoiceIndex(SettingsService.RECOMP_VR_BULLET_BILL_CAMERA, BulletBillValues);
            RotationDropdown.ItemsSource = new[] { "Turning only", "Turning + pitch", "Full rotation" };
            RotationDropdown.SelectedIndex = ChoiceIndex(SettingsService.RECOMP_VR_FIRST_PERSON_ROTATION, RotationValues);
            HideDropdown.ItemsSource = new[] { "Nothing", "Driver", "Driver and kart" };
            HideDropdown.SelectedIndex = (int)ReadHiding();
            SeatDropdown.ItemsSource = new[] { "Cockpit", "Custom" };
            SeatDropdown.SelectedIndex = ChoiceIndex(SettingsService.RECOMP_VR_FIRST_PERSON_SEAT, SeatValues);
            BodyIkSwitch.IsChecked = SettingsService.Get(SettingsService.RECOMP_VR_BODY_IK);
            BodyIkHandsOnlySwitch.IsChecked = SettingsService.Get(SettingsService.RECOMP_VR_BODY_IK_HANDS_ONLY);
            PlaceholderWheelSwitch.IsChecked = SettingsService.Get(SettingsService.RECOMP_VR_PLACEHOLDER_STEERING_WHEEL);
            HandSteeringSwitch.IsChecked = SettingsService.Get(SettingsService.RECOMP_VR_HAND_STEERING);
            ItemHandDropdown.ItemsSource = new[] { "Left", "Right", "Off" };
            ItemHandDropdown.SelectedIndex = ChoiceIndex(SettingsService.RECOMP_VR_COCKPIT_ITEM_HAND, ItemHandValues);
            ItemThrowSwitch.IsChecked = SettingsService.Get(SettingsService.RECOMP_VR_COCKPIT_ITEM_THROW);
            _sliderSave.Stop();
            _pendingSliders.Clear();
            foreach (var row in _sliders)
            {
                var value = SettingsService.Get(row.Setting);
                row.Slider.Value = value;
                row.ValueText.Text = Describe(row, value);
            }
            RefreshDependents();
        }
        finally
        {
            _loading = false;
        }
    }

    // One setting in two keys, presented as the in-game panel's two tick boxes are: nothing, the
    // driver (model 0), or every model (-1).
    private Hiding ReadHiding() =>
        !SettingsService.Get(SettingsService.RECOMP_VR_HIDE_DRIVER) ? Hiding.Nothing
        : SettingsService.Get(SettingsService.RECOMP_VR_FIRST_PERSON_HIDDEN_MODEL) < 0 ? Hiding.DriverAndKart
        : Hiding.Driver;

    // A name the runtime does not accept reads as its default, as it does in game.
    private int ChoiceIndex(Setting<string> setting, string[] values)
    {
        var index = Array.IndexOf(values, SettingsService.Get(setting));
        return index >= 0 ? index : Array.IndexOf(values, setting.DefaultValue);
    }

    private static string Describe(SliderRow row, double value) => string.Format(CultureInfo.InvariantCulture, row.Format, value);

    private void RefreshDependents()
    {
        // Flat Screen mode (the OpenXR VR tab's race view) keeps races on the menu screen, which none
        // of these rows reach.
        var immersive = !SettingsService.Get(SettingsService.RECOMP_VR_FLAT_SCREEN);
        // first_person is only the camera a race starts on: a click switches any race into first
        // person, so its rows apply whichever default is chosen.
        var firstPerson = immersive;
        // The steering wheel, hand steering, the held item and the character body belong to the
        // cockpit seat.
        var cockpit = firstPerson && SettingsService.Get(SettingsService.RECOMP_VR_FIRST_PERSON_SEAT) == "cockpit";
        var characterBody = cockpit && SettingsService.Get(SettingsService.RECOMP_VR_BODY_IK);
        var handSteering = cockpit && SettingsService.Get(SettingsService.RECOMP_VR_HAND_STEERING);
        var itemShown = cockpit && SettingsService.Get(SettingsService.RECOMP_VR_COCKPIT_ITEM_HAND) != "off";

        LeanBackRow.IsEnabled = immersive;
        DefaultCameraRow.IsEnabled = immersive;
        BulletBillRow.IsEnabled = firstPerson;
        RotationRow.IsEnabled = firstPerson;
        // As in game, greyed out while the character body takes the driver over.
        HideRow.IsEnabled = firstPerson && !characterBody;
        SeatRow.IsEnabled = firstPerson;
        BodyIkRow.IsEnabled = cockpit;
        BodyIkHandsOnlyRow.IsEnabled = characterBody;
        LeftHandRollRow.IsEnabled = characterBody;
        RightHandRollRow.IsEnabled = characterBody;
        PlaceholderWheelRow.IsEnabled = cockpit;
        HandSteeringRow.IsEnabled = cockpit;
        WheelKartRow.IsEnabled = handSteering;
        WheelBikeRow.IsEnabled = handSteering;
        ItemHandRow.IsEnabled = cockpit;
        ItemThrowRow.IsEnabled = itemShown;
        ItemSizeRow.IsEnabled = itemShown;
    }

    private Func<bool> Write<T>(Setting<T> setting, T value) => () => SettingsService.Set(setting, value);

    private void SaveChoice(Setting<string> setting, ComboBox dropdown, string[] values)
    {
        if (dropdown.SelectedIndex >= 0 && dropdown.SelectedIndex < values.Length)
            Save(Write(setting, values[dropdown.SelectedIndex]));
    }

    private void SaveHiding()
    {
        switch ((Hiding)HideDropdown.SelectedIndex)
        {
            case Hiding.Nothing:
                Save(Write(SettingsService.RECOMP_VR_HIDE_DRIVER, false));
                break;
            case Hiding.Driver:
                Save(Write(SettingsService.RECOMP_VR_HIDE_DRIVER, true), Write(SettingsService.RECOMP_VR_FIRST_PERSON_HIDDEN_MODEL, 0));
                break;
            case Hiding.DriverAndKart:
                Save(Write(SettingsService.RECOMP_VR_HIDE_DRIVER, true), Write(SettingsService.RECOMP_VR_FIRST_PERSON_HIDDEN_MODEL, -1));
                break;
        }
    }

    private void SliderMoved(SliderRow row)
    {
        // Rounded to the slider's step, so 0.28 is written as 0.28.
        var value = Math.Round(row.Slider.Value, row.Decimals);
        row.ValueText.Text = Describe(row, value);
        if (_loading)
            return;
        _pendingSliders[row.Setting] = value;
        _sliderSave.Stop();
        _sliderSave.Start();
    }

    private void SavePendingSliders()
    {
        _sliderSave.Stop();
        if (_pendingSliders.Count == 0)
            return;
        var writes = _pendingSliders.Select(pending => Write(pending.Key, pending.Value)).ToArray();
        _pendingSliders.Clear();
        Save(writes);
    }

    private void Save(params Func<bool>[] writes)
    {
        if (_loading || !IsVrBackend || !IsInstalled)
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
        RefreshDependents();
    }

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
}
