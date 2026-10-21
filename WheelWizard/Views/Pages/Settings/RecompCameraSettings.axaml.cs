using System.Globalization;
using Avalonia.Controls;
using Avalonia.Threading;
using WheelWizard.Recomp;
using WheelWizard.Settings;
using WheelWizard.Settings.Types;
using WheelWizard.Shared.DependencyInjection;
using WheelWizard.Views.Popups.Generic;

namespace WheelWizard.Views.Pages.Settings;

/// <summary>
/// The OpenXR VR race camera, on its own tab as in the game's own settings panel, with the rows of
/// the Quest launcher's Camera tab (tracked hands are Quest-only). Like the OpenXR VR tab, it writes
/// the VR installation's Config.toml, and only once that installation exists. A row is greyed out
/// while the choices above it leave it nothing to do, as on the Quest.
/// </summary>
public partial class RecompCameraSettings : UserControlBase
{
    private enum RaceView
    {
        Immersive,
        ImmersiveWindow,
        FlatScreen,
    }

    private enum Hiding
    {
        Nothing,
        Driver,
        DriverAndKart,
    }

    private sealed record SliderRow(Slider Slider, TextBlock ValueText, Setting Setting, string Format, int Decimals);

    private static readonly string[] RotationValues = ["yaw", "yaw_pitch", "full"];
    private static readonly string[] BulletBillValues = ["inside", "game"];
    private static readonly string[] SeatValues = ["cockpit", "custom"];
    private static readonly string[] ItemHandValues = ["left", "right", "off"];

    private bool _loading;
    private readonly List<SliderRow> _sliders;

    // A slider writes once it settles rather than at every step of a drag.
    private readonly DispatcherTimer _sliderSave = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly Dictionary<Setting, double> _pendingSliders = [];

    [Inject]
    private ISettingsManager SettingsService { get; set; } = null!;

    [Inject]
    private IRecompSettingManager RecompSettingsFile { get; set; } = null!;

    [Inject]
    private IRecompEnvironment? RecompEnvironment { get; set; }

    [Inject]
    private IRecompInstallService? RecompInstallService { get; set; }

    public RecompCameraSettings()
    {
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
        RecompSettingsFile.ReloadSettings();
        LoadSettings();

        // Attached after loading, so populating a control never writes it straight back.
        RaceViewDropdown.SelectionChanged += (_, _) =>
        {
            // One setting in two keys, as runtime_config.h's VrRaceView reads them.
            var view = (RaceView)RaceViewDropdown.SelectedIndex;
            if (RaceViewDropdown.SelectedIndex >= 0)
                Save(
                    (SettingsService.RECOMP_VR_FLAT_SCREEN, view == RaceView.FlatScreen),
                    (SettingsService.RECOMP_VR_IMMERSIVE_WINDOW, view == RaceView.ImmersiveWindow)
                );
        };
        RaceIntroDropdown.SelectionChanged += (_, _) =>
        {
            if (RaceIntroDropdown.SelectedIndex >= 0)
                Save((SettingsService.RECOMP_VR_FLAT_RACE_INTRO, RaceIntroDropdown.SelectedIndex == 1));
        };
        DefaultCameraDropdown.SelectionChanged += (_, _) =>
        {
            if (DefaultCameraDropdown.SelectedIndex >= 0)
                Save((SettingsService.RECOMP_VR_FIRST_PERSON, DefaultCameraDropdown.SelectedIndex == 1));
        };
        BulletBillDropdown.SelectionChanged += (_, _) =>
            SaveChoice(SettingsService.RECOMP_VR_BULLET_BILL_CAMERA, BulletBillDropdown, BulletBillValues);
        RotationDropdown.SelectionChanged += (_, _) =>
            SaveChoice(SettingsService.RECOMP_VR_FIRST_PERSON_ROTATION, RotationDropdown, RotationValues);
        HideDropdown.SelectionChanged += (_, _) => SaveHiding();
        SeatDropdown.SelectionChanged += (_, _) => SaveChoice(SettingsService.RECOMP_VR_FIRST_PERSON_SEAT, SeatDropdown, SeatValues);
        ItemHandDropdown.SelectionChanged += (_, _) =>
            SaveChoice(SettingsService.RECOMP_VR_COCKPIT_ITEM_HAND, ItemHandDropdown, ItemHandValues);
        BodyIkSwitch.IsCheckedChanged += (_, _) => Save((SettingsService.RECOMP_VR_BODY_IK, BodyIkSwitch.IsChecked == true));
        BodyIkHandsOnlySwitch.IsCheckedChanged += (_, _) =>
            Save((SettingsService.RECOMP_VR_BODY_IK_HANDS_ONLY, BodyIkHandsOnlySwitch.IsChecked == true));
        PlaceholderWheelSwitch.IsCheckedChanged += (_, _) =>
            Save((SettingsService.RECOMP_VR_PLACEHOLDER_STEERING_WHEEL, PlaceholderWheelSwitch.IsChecked == true));
        HandSteeringSwitch.IsCheckedChanged += (_, _) =>
            Save((SettingsService.RECOMP_VR_HAND_STEERING, HandSteeringSwitch.IsChecked == true));
        ItemThrowSwitch.IsCheckedChanged += (_, _) =>
            Save((SettingsService.RECOMP_VR_COCKPIT_ITEM_THROW, ItemThrowSwitch.IsChecked == true));
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

    private T Get<T>(Setting setting) => SettingsService.Get<T>(setting);

    private void LoadSettings()
    {
        _loading = true;
        try
        {
            CameraSection.IsEnabled = IsVrBackend && IsInstalled;
            RaceViewDropdown.ItemsSource = new[] { "Immersive", "Immersive window", "Flat screen" };
            RaceViewDropdown.SelectedIndex = (int)ReadRaceView();
            RaceIntroDropdown.ItemsSource = new[] { "Immersive", "Flat screen" };
            RaceIntroDropdown.SelectedIndex = Get<bool>(SettingsService.RECOMP_VR_FLAT_RACE_INTRO) ? 1 : 0;
            DefaultCameraDropdown.ItemsSource = new[] { "Game camera", "First person" };
            DefaultCameraDropdown.SelectedIndex = Get<bool>(SettingsService.RECOMP_VR_FIRST_PERSON) ? 1 : 0;
            BulletBillDropdown.ItemsSource = new[] { "Ride inside", "Game camera" };
            BulletBillDropdown.SelectedIndex = ChoiceIndex(SettingsService.RECOMP_VR_BULLET_BILL_CAMERA, BulletBillValues);
            RotationDropdown.ItemsSource = new[] { "Turning only", "Turning + pitch", "Full rotation" };
            RotationDropdown.SelectedIndex = ChoiceIndex(SettingsService.RECOMP_VR_FIRST_PERSON_ROTATION, RotationValues);
            HideDropdown.ItemsSource = new[] { "Nothing", "Driver", "Driver and kart" };
            HideDropdown.SelectedIndex = (int)ReadHiding();
            SeatDropdown.ItemsSource = new[] { "Cockpit", "Custom" };
            SeatDropdown.SelectedIndex = ChoiceIndex(SettingsService.RECOMP_VR_FIRST_PERSON_SEAT, SeatValues);
            BodyIkSwitch.IsChecked = Get<bool>(SettingsService.RECOMP_VR_BODY_IK);
            BodyIkHandsOnlySwitch.IsChecked = Get<bool>(SettingsService.RECOMP_VR_BODY_IK_HANDS_ONLY);
            PlaceholderWheelSwitch.IsChecked = Get<bool>(SettingsService.RECOMP_VR_PLACEHOLDER_STEERING_WHEEL);
            HandSteeringSwitch.IsChecked = Get<bool>(SettingsService.RECOMP_VR_HAND_STEERING);
            ItemHandDropdown.ItemsSource = new[] { "Left", "Right", "Off" };
            ItemHandDropdown.SelectedIndex = ChoiceIndex(SettingsService.RECOMP_VR_COCKPIT_ITEM_HAND, ItemHandValues);
            ItemThrowSwitch.IsChecked = Get<bool>(SettingsService.RECOMP_VR_COCKPIT_ITEM_THROW);
            _sliderSave.Stop();
            _pendingSliders.Clear();
            foreach (var row in _sliders)
            {
                var value = Get<double>(row.Setting);
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

    // Flat Screen mode wins over the immersive window, as in runtime_config.h's VrRaceView.
    private RaceView ReadRaceView() =>
        Get<bool>(SettingsService.RECOMP_VR_FLAT_SCREEN) ? RaceView.FlatScreen
        : Get<bool>(SettingsService.RECOMP_VR_IMMERSIVE_WINDOW) ? RaceView.ImmersiveWindow
        : RaceView.Immersive;

    // One setting in two keys, presented as the in-game panel's two tick boxes are: nothing, the
    // driver (model 0), or every model (-1).
    private Hiding ReadHiding() =>
        !Get<bool>(SettingsService.RECOMP_VR_HIDE_DRIVER) ? Hiding.Nothing
        : Get<int>(SettingsService.RECOMP_VR_FIRST_PERSON_HIDDEN_MODEL) < 0 ? Hiding.DriverAndKart
        : Hiding.Driver;

    // A name the runtime does not accept reads as its default, as it does in game.
    private int ChoiceIndex(Setting setting, string[] values)
    {
        var index = Array.IndexOf(values, Get<string>(setting));
        return index >= 0 ? index : Array.IndexOf(values, setting.DefaultValue);
    }

    private static string Describe(SliderRow row, double value) => string.Format(CultureInfo.InvariantCulture, row.Format, value);

    private void RefreshDependents()
    {
        // Flat Screen mode keeps races on the menu screen, which none of the rows below reach.
        var immersive = !Get<bool>(SettingsService.RECOMP_VR_FLAT_SCREEN);
        // first_person is only the camera a race starts on: a click switches any race into first
        // person, so its rows apply whichever default is chosen.
        var firstPerson = immersive;
        // The steering wheel, hand steering, the held item and the character body belong to the
        // cockpit seat.
        var cockpit = firstPerson && Get<string>(SettingsService.RECOMP_VR_FIRST_PERSON_SEAT) == "cockpit";
        var characterBody = cockpit && Get<bool>(SettingsService.RECOMP_VR_BODY_IK);
        var handSteering = cockpit && Get<bool>(SettingsService.RECOMP_VR_HAND_STEERING);
        var itemShown = cockpit && Get<string>(SettingsService.RECOMP_VR_COCKPIT_ITEM_HAND) != "off";

        RaceIntroRow.IsEnabled = immersive;
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

    private void SaveChoice(Setting setting, ComboBox dropdown, string[] values)
    {
        if (dropdown.SelectedIndex >= 0 && dropdown.SelectedIndex < values.Length)
            Save((setting, values[dropdown.SelectedIndex]));
    }

    private void SaveHiding()
    {
        switch ((Hiding)HideDropdown.SelectedIndex)
        {
            case Hiding.Nothing:
                Save((SettingsService.RECOMP_VR_HIDE_DRIVER, false));
                break;
            case Hiding.Driver:
                Save((SettingsService.RECOMP_VR_HIDE_DRIVER, true), (SettingsService.RECOMP_VR_FIRST_PERSON_HIDDEN_MODEL, 0));
                break;
            case Hiding.DriverAndKart:
                Save((SettingsService.RECOMP_VR_HIDE_DRIVER, true), (SettingsService.RECOMP_VR_FIRST_PERSON_HIDDEN_MODEL, -1));
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
        var values = _pendingSliders.Select(pending => (pending.Key, (object)pending.Value)).ToArray();
        _pendingSliders.Clear();
        Save(values);
    }

    private void Save(params (Setting Setting, object Value)[] values)
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
        foreach (var (setting, value) in values)
            SettingsService.Set(setting, value);
        RefreshDependents();
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
