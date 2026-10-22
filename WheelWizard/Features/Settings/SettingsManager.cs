using System.IO.Abstractions;
using WheelWizard.ApplicationData;
using WheelWizard.Dolphin.Paths;
using WheelWizard.DolphinInstaller;
using WheelWizard.Models.Enums;
using WheelWizard.Recomp;
using WheelWizard.Settings.Types;
using WheelWizard.Shared.IO;
using WheelWizard.Shared.Platform;
using WheelWizard.Shared.Processes;

namespace WheelWizard.Settings;

public class SettingsManager : ISettingsManager, IDisposable
{
    private readonly ISettingsSignalBus _signalBus;
    private readonly List<Setting> _ownedSettings = [];
    private readonly IWhWzSettingManager _whWzSettingManager;
    private readonly IDolphinSettingManager _dolphinSettingManager;
    private readonly IRecompSettingManager _recompSettingManager;
    private readonly IFileSystem _fileSystem;
    private readonly IDolphinPathResolver _dolphinPaths;
    private readonly IApplicationDataLocation _applicationData;
    private readonly IRecompPaths _recompPaths;
    private readonly IRuntimeEnvironment _environment;
    private readonly RecompBackendSelection _backendSelection;
    private bool IsFlatpakSandboxed => _environment.IsFlatpakSandboxed(_fileSystem);
    private bool IsMissingExtensionForDolphin => _environment.IsMissingDolphinFlatpakExtension(_fileSystem);
    private bool IsMissingExtensionForRecomp => _environment.IsMissingRecompFlatpakExtension(_fileSystem);

    private readonly Setting<DolphinShaderCompilationMode> _dolphinCompilationMode;
    private readonly Setting<bool> _dolphinCompileShadersAtStart;
    private readonly Setting<bool> _dolphinSsaa;
    private readonly Setting<string> _dolphinMsaa;

    private bool _hasLoadedSettings;
    private double _internalScale = -1.0;

    #region Constructor
    public SettingsManager(
        IWhWzSettingManager whWzSettingManager,
        IDolphinSettingManager dolphinSettingManager,
        IRecompSettingManager recompSettingManager,
        IFileSystem fileSystem,
        ISettingsSignalBus signalBus,
        IDolphinPathResolver dolphinPaths,
        IApplicationDataLocation applicationData,
        IRecompPaths recompPaths,
        IRuntimeEnvironment environment,
        IUnixCommandService commands,
        RecompBackendSelection? backendSelection = null
    )
    {
        _whWzSettingManager = whWzSettingManager;
        _dolphinSettingManager = dolphinSettingManager;
        _recompSettingManager = recompSettingManager;
        _fileSystem = fileSystem;
        _signalBus = signalBus;
        _dolphinPaths = dolphinPaths;
        _applicationData = applicationData;
        _recompPaths = recompPaths;
        _environment = environment;
        _backendSelection = backendSelection ?? new RecompBackendSelection(applicationData, recompPaths);

        #region WhWz settings
        // Register this first because the path validators use the active frontend mode when deciding
        // whether Dolphin-only locations may be left blank.
        ENABLE_RECOMP = RegisterWhWz("EnableRecomp", false);
        // This build is the VR launcher, so its own backend is the one it starts on. Only Windows can
        // run it (IsRecompModeActive checks that), and an explicit choice is saved and wins as usual.
        ENABLE_RECOMP_VR = RegisterWhWz("EnableRecompVR", true);
        // Whether WiiCompiled directly shares Dolphin's live NAND. Disabled means private mode;
        // private mode uses the imported clone below when one exists, otherwise the runtime default.
        RECOMP_USE_DOLPHIN_DATA = RegisterWhWz("RecompUseDolphinData", false);
        // Whether private mode was initialized from the Wheel Wizard-owned Dolphin clone.
        RECOMP_COPY_DOLPHIN_NAND = RegisterWhWz("RecompCopyDolphinNand", false);
        // Which Meta Quest Build for Quest fetches the app for, as RecompQuestHeadsets' setting values.
        // The modern app is the default; the original Quest's is a separate app with its own kit.
        RECOMP_QUEST_HEADSET = RegisterWhWz("RecompQuestHeadset", RecompQuestHeadsets.ModernSettingValue);
        DOLPHIN_LOCATION = RegisterWhWz(
            "DolphinLocation",
            // Use the wrapper for the Flatpak as the default value as a hint for curious users
            IsFlatpakSandboxed ? "/app/extensions/backends/dolphin-emu/bin/dolphin-emu-wrapper" : "",
            value =>
            {
                if (IsFlatpakSandboxed)
                {
                    // The Dolphin location setting is ignored with the Flatpak since it bundles a separate Dolphin
                    return true;
                }
                var pathOrCommand = value;
                if (string.IsNullOrWhiteSpace(pathOrCommand))
                    return IsRecompModeActive();

                if (environment.IsLinux || environment.IsMacOS)
                {
                    return commands.IsCommandAvailable(pathOrCommand);
                }

                return _fileSystem.File.Exists(pathOrCommand);
            }
        );

        USER_FOLDER_PATH = RegisterWhWz(
            "UserFolderPath",
            "",
            value =>
            {
                var userFolderPath = value;
                if (string.IsNullOrWhiteSpace(userFolderPath))
                    return IsRecompModeActive();
                if (!_fileSystem.Directory.Exists(userFolderPath))
                    return false;

                var dolphinLocation = Get<string>(DOLPHIN_LOCATION);
                var dolphinPaths = _dolphinPaths.Resolve(dolphinLocation, userFolderPath);

                // We cannot determine the validity of the user folder path in that case
                if (!IsFlatpakSandboxed && string.IsNullOrWhiteSpace(dolphinLocation))
                    return true;

                // If we want to use a split XDG dolphin config,
                // this only really works as expected if certain conditions are met.
                // Note that the Wheel Wizard Flatpak always uses the split config internally, so it cannot return early here.
                if (!environment.IsLinux || !dolphinPaths.IsLinuxDolphinConfigSplit())
                    return true;

                if (IsFlatpakSandboxed)
                {
                    // Reject the internal Dolphin directory symlink paths
                    foreach (var blockedUserFolder in dolphinPaths.LinuxFlatpakSandboxedDolphinUserFolderBlockList)
                    {
                        // XXX: Circular symlink references may stil break the Flatpak, but they
                        // shouldn't be present under normal usage.
                        if (
                            _fileSystem
                                .Path.NormalizePath(blockedUserFolder)
                                .Equals(_fileSystem.Path.NormalizePath(userFolderPath), StringComparison.Ordinal)
                        )
                        {
                            return false;
                        }
                    }
                }

                // In this case, Dolphin would use `EMBEDDED_USER_DIR` (portable `user` directory).
                if (_fileSystem.Directory.Exists("user"))
                    return false;

                // The Dolphin executable directory with `portable.txt` case
                if (
                    !IsFlatpakSandboxed
                    && _fileSystem.File.Exists(_fileSystem.Path.Combine(dolphinPaths.GetDolphinExeDirectory(), "portable.txt"))
                )
                    return false;

                if (!IsFlatpakSandboxed)
                {
                    // The Wheel Wizard Flatpak's wrapper unsets this.
                    // The value of this environment variable would be used instead if it was somehow set.
                    const string environmentVariableToAvoid = "DOLPHIN_EMU_USERPATH";

                    if (!string.IsNullOrWhiteSpace(environment.GetEnvironmentVariable(environmentVariableToAvoid)))
                        return false;

                    if (dolphinLocation.Contains(environmentVariableToAvoid, StringComparison.Ordinal))
                        return false;
                }

                // `~/.dolphin-emu` would be used if it exists
                var legacyFolderPath = dolphinPaths.LinuxDolphinLegacyFolderPath;
                if (_fileSystem.Directory.Exists(legacyFolderPath))
                {
                    if (IsFlatpakSandboxed)
                    {
                        if (
                            !string.IsNullOrWhiteSpace(dolphinPaths.SplitLinuxDolphinConfigDir)
                            && dolphinPaths.SplitLinuxDolphinConfigDir.Equals(
                                dolphinPaths.SplitLinuxDolphinNativeConfigDir,
                                StringComparison.Ordinal
                            )
                        )
                        {
                            // In this case, the user requested native Dolphin's split config/user folders (not `~/.dolphin-emu`).
                            // Since Flatpak may leave an empty `~/.dolphin-emu` folder around, we need to check
                            // if it is empty and remove it, so our bundled Dolphin does not use it.
                            if (!_fileSystem.IsDirectoryEmpty(legacyFolderPath))
                            {
                                return false;
                            }

                            try
                            {
                                // Remove the offending empty directory
                                _fileSystem.Directory.Delete(legacyFolderPath);
                            }
                            catch (DirectoryNotFoundException)
                            {
                                // We let this pass
                            }
                            catch (Exception)
                            {
                                return false;
                            }
                        }
                    }
                    else if (!dolphinPaths.IsFlatpakDolphinFilePath(dolphinLocation))
                    {
                        // The official Dolphin Flatpak ignores the `~/.dolphin-emu` folder, so only return
                        // false if it is not a Flatpak Dolphin executable
                        return false;
                    }
                }

                return true;
            }
        );

        GAME_LOCATION = RegisterWhWz("GameLocation", "", value => _fileSystem.File.Exists(value));
        FORCE_WIIMOTE = RegisterWhWz("ForceWiimote", false);
        LAUNCH_WITH_DOLPHIN = RegisterWhWz("LaunchWithDolphin", false);
        LAUNCH_RR_ON_STARTUP = RegisterWhWz("LaunchRrOnStartup", false);
        PREFERS_MODS_ROW_VIEW = RegisterWhWz("PrefersModsRowView", true);
        USE_PATCHES_SYSTEM = RegisterWhWz("UsePatchesSystem", false);
        FOCUSED_USER = RegisterWhWz("FavoriteUser", 0, value => value >= 0 && value < 4);

        ENABLE_ANIMATIONS = RegisterWhWz("EnableAnimations", true);
        SIDEBAR_COLLAPSED = RegisterWhWz("SidebarCollapsed", false);
        TESTING_MODE_ENABLED = RegisterWhWz("TestingModeEnabled", false);
        SAVED_WINDOW_SCALE = RegisterWhWz("WindowScale", 1.0, SettingValues.IsValidWindowScale);
        RR_REGION = RegisterWhWz("RR_Region", MarioKartWiiEnums.Regions.None);
        WW_LANGUAGE = RegisterWhWz("WW_Language", "en", value => SettingValues.WhWzLanguages.ContainsKey(value));
        #endregion

        #region Dolphin settings
        NAND_ROOT_PATH = RegisterDolphin(("Dolphin.ini", "General", "NANDRootPath"), "", value => _fileSystem.Directory.Exists(value));

        LOAD_PATH = RegisterDolphin(("Dolphin.ini", "General", "LoadPath"), "", value => _fileSystem.Directory.Exists(value));

        VSYNC = RegisterDolphin(("GFX.ini", "Hardware", "VSync"), false);
        INTERNAL_RESOLUTION = RegisterDolphin(("GFX.ini", "Settings", "InternalResolution"), 1, value => value >= 0);
        SHOW_FPS = RegisterDolphin(("GFX.ini", "Settings", "ShowFPS"), false);
        GFX_BACKEND = RegisterDolphin(("Dolphin.ini", "Core", "GFXBackend"), SettingValues.GFXRenderers.Values.First());

        // recommended settings
        _dolphinCompilationMode = RegisterDolphin(("GFX.ini", "Settings", "ShaderCompilationMode"), DolphinShaderCompilationMode.Default);
        _dolphinCompileShadersAtStart = RegisterDolphin(("GFX.ini", "Settings", "WaitForShadersBeforeStarting"), false);
        _dolphinSsaa = RegisterDolphin(("GFX.ini", "Settings", "SSAA"), false);
        _dolphinMsaa = RegisterDolphin(
            ("GFX.ini", "Settings", "MSAA"),
            "0x00000001",
            value => (value) is "0x00000001" or "0x00000002" or "0x00000004" or "0x00000008"
        );

        // Readonly settings
        // #todo: (#377) validate blank mac settings and guide recovery before mii creation; add a regression test.
        MACADDRESS = RegisterDolphin(("Dolphin.ini", "General", "WirelessMac"), "02:01:02:03:04:05");
        #endregion

        #region Recomp settings
        // Stored in the recomp's own Config.toml, which the in-game settings bar also writes.
        // Defaults mirror the runtime's own fallbacks, so an absent key reads the same here as in game.
        RECOMP_RESOLUTION_MULTIPLIER = RegisterRecomp(("video", "resolution_multiplier"), 1.0);
        RECOMP_GRAPHICS_API = RegisterRecomp(("video", "graphics_api"), "auto");
        RECOMP_SHOW_FPS = RegisterRecomp(("video", "show_fps"), true);
        RECOMP_PREVENT_STUTTERS = RegisterRecomp(("video", "skip_unready_pipelines"), true);
        // Managed VR installations enable OpenXR initially; explicit desktop choices are retained.
        RECOMP_VR_ENABLED = RegisterRecomp(("vr", "enabled"), true);
        RECOMP_VR_MIRROR_VIEW = RegisterRecomp(("vr", "mirror_view"), "normal");
        RECOMP_VR_FIRST_PERSON = RegisterRecomp(("vr", "first_person"), true);
        RECOMP_VR_FIRST_PERSON_ROTATION = RegisterRecomp(("vr", "first_person_rotation"), "yaw_pitch");
        RECOMP_VR_HIDE_DRIVER = RegisterRecomp(("vr", "first_person_hide_driver"), true);
        // Hand steering (grab the cockpit's wheel with the VR controllers), by heurazy. On in game too:
        // the stick keeps steering until a grip takes hold of the wheel.
        RECOMP_VR_HAND_STEERING = RegisterRecomp(("vr", "hand_steering"), true);
        RECOMP_VR_RENDER_SCALE = RegisterRecomp(("vr", "render_scale"), 1.0);
        // The rest of the race camera, as runtime_config.h reads it: the same defaults, and a value
        // outside the range or the names it accepts keeps the default, as the runtime discards it.
        RECOMP_VR_FLAT_SCREEN = RegisterRecomp(("vr", "flat_screen"), false);
        RECOMP_VR_IMMERSIVE_WINDOW = RegisterRecomp(("vr", "immersive_window"), false);
        RECOMP_VR_FLAT_RACE_INTRO = RegisterRecomp(("vr", "flat_race_intro"), true);
        RECOMP_VR_LEAN_BACK_DEGREES = RegisterRecomp(("vr", "lean_back_degrees"), 0.0, InRange(-45.0, 45.0));
        RECOMP_VR_BULLET_BILL_CAMERA = RegisterRecomp(("vr", "bullet_bill_camera"), "game", OneOf("inside", "game"));
        RECOMP_VR_FIRST_PERSON_HIDDEN_MODEL = RegisterRecomp(("vr", "first_person_hidden_model"), 0, model => model >= -1 && model <= 31);
        RECOMP_VR_FIRST_PERSON_SEAT = RegisterRecomp(("vr", "first_person_seat"), "cockpit", OneOf("cockpit", "custom"));
        RECOMP_VR_BODY_IK = RegisterRecomp(("vr", "body_ik"), true);
        RECOMP_VR_BODY_IK_HANDS_ONLY = RegisterRecomp(("vr", "body_ik_hands_only"), false);
        RECOMP_VR_BODY_IK_LEFT_HAND_ROLL = RegisterRecomp(("vr", "body_ik_left_hand_roll"), 90.0, InRange(-180.0, 180.0));
        RECOMP_VR_BODY_IK_RIGHT_HAND_ROLL = RegisterRecomp(("vr", "body_ik_right_hand_roll"), -90.0, InRange(-180.0, 180.0));
        RECOMP_VR_PLACEHOLDER_STEERING_WHEEL = RegisterRecomp(("vr", "placeholder_steering_wheel"), false);
        RECOMP_VR_WHEEL_KART_DEGREES = RegisterRecomp(("vr", "wheel_kart_degrees"), 45.0, InRange(20.0, 180.0));
        RECOMP_VR_WHEEL_BIKE_DEGREES = RegisterRecomp(("vr", "wheel_bike_degrees"), 45.0, InRange(20.0, 180.0));
        RECOMP_VR_COCKPIT_ITEM_HAND = RegisterRecomp(("vr", "cockpit_item_hand"), "right", OneOf("left", "right", "off"));
        RECOMP_VR_COCKPIT_ITEM_THROW = RegisterRecomp(("vr", "cockpit_item_throw"), true);
        RECOMP_VR_COCKPIT_ITEM_SIZE = RegisterRecomp(("vr", "cockpit_item_size"), 0.2, InRange(0.05, 0.60));
        // The Wii data folder the runtime should use, written by RecompDolphinDataService after an
        // install and whenever the sharing choice changes. Empty/absent means the runtime's private NAND.
        RECOMP_NAND_ROOT = RegisterRecomp(("paths", "nand_root"), "");
        #endregion

        #region Virtual settings
        var windowScale = new VirtualSetting<double>(
            value => _internalScale = value,
            () => _internalScale == -1.0 ? SAVED_WINDOW_SCALE.Get() : _internalScale
        );
        windowScale.SetValidation(SettingValues.IsValidWindowScale);
        WINDOW_SCALE = windowScale.SetDependencies(SAVED_WINDOW_SCALE);

        RECOMMENDED_SETTINGS = new VirtualSetting<bool>(
            value =>
            {
                var newValue = value;
                SaveRecommended(
                    _dolphinCompilationMode,
                    newValue ? DolphinShaderCompilationMode.HybridUberShaders : DolphinShaderCompilationMode.Default
                );
#if WINDOWS
                SaveRecommended(_dolphinCompileShadersAtStart, newValue);
#endif
                SaveRecommended(_dolphinMsaa, newValue ? "0x00000002" : "0x00000001");
                SaveRecommended(_dolphinSsaa, false);
            },
            () =>
            {
                var value1 = (DolphinShaderCompilationMode)_dolphinCompilationMode.Get();
                var value2 = true;
#if WINDOWS
                value2 = (bool)_dolphinCompileShadersAtStart.Get();
#endif
                var value3 = (string)_dolphinMsaa.Get();
                var value4 = (bool)_dolphinSsaa.Get();
                return !value4 && value2 && value3 == "0x00000002" && value1 == DolphinShaderCompilationMode.HybridUberShaders;
            }
        ).SetDependencies(_dolphinCompilationMode, _dolphinCompileShadersAtStart, _dolphinMsaa, _dolphinSsaa);
        _ownedSettings.Add(WINDOW_SCALE);
        _ownedSettings.Add(RECOMMENDED_SETTINGS);
        foreach (var setting in _ownedSettings)
            setting.Changed += _signalBus.Publish;
        #endregion
    }
    #endregion

    private static void SaveRecommended<T>(Setting<T> setting, T value)
    {
        if (!setting.Set(value))
            throw setting.SaveError ?? new IOException($"Could not save {setting.Name}.");
    }

    #region Settings Properties
    public Setting<string> USER_FOLDER_PATH { get; }
    public Setting<string> DOLPHIN_LOCATION { get; }
    public Setting<string> GAME_LOCATION { get; }
    public Setting<bool> FORCE_WIIMOTE { get; }
    public Setting<bool> LAUNCH_WITH_DOLPHIN { get; }
    public Setting<bool> LAUNCH_RR_ON_STARTUP { get; }
    public Setting<bool> ENABLE_RECOMP { get; }
    public Setting<bool> ENABLE_RECOMP_VR { get; }
    public Setting<bool> RECOMP_USE_DOLPHIN_DATA { get; }
    public Setting<bool> RECOMP_COPY_DOLPHIN_NAND { get; }
    public Setting<string> RECOMP_QUEST_HEADSET { get; }
    public Setting<bool> PREFERS_MODS_ROW_VIEW { get; }
    public Setting<bool> USE_PATCHES_SYSTEM { get; }
    public Setting<int> FOCUSED_USER { get; }
    public Setting<bool> ENABLE_ANIMATIONS { get; }
    public Setting<bool> SIDEBAR_COLLAPSED { get; }
    public Setting<bool> TESTING_MODE_ENABLED { get; }
    public Setting<double> SAVED_WINDOW_SCALE { get; }
    public Setting<MarioKartWiiEnums.Regions> RR_REGION { get; }
    public Setting<string> WW_LANGUAGE { get; }

    public Setting<string> NAND_ROOT_PATH { get; }
    public Setting<string> LOAD_PATH { get; }
    public Setting<bool> VSYNC { get; }
    public Setting<int> INTERNAL_RESOLUTION { get; }
    public Setting<bool> SHOW_FPS { get; }
    public Setting<string> GFX_BACKEND { get; }
    public Setting<string> MACADDRESS { get; }
    public Setting<double> WINDOW_SCALE { get; }
    public Setting<bool> RECOMMENDED_SETTINGS { get; }
    public Setting<double> RECOMP_RESOLUTION_MULTIPLIER { get; }
    public Setting<string> RECOMP_GRAPHICS_API { get; }
    public Setting<bool> RECOMP_SHOW_FPS { get; }
    public Setting<bool> RECOMP_PREVENT_STUTTERS { get; }
    public Setting<string> RECOMP_NAND_ROOT { get; }
    public Setting<bool> RECOMP_VR_ENABLED { get; }
    public Setting<string> RECOMP_VR_MIRROR_VIEW { get; }
    public Setting<bool> RECOMP_VR_FIRST_PERSON { get; }
    public Setting<string> RECOMP_VR_FIRST_PERSON_ROTATION { get; }
    public Setting<bool> RECOMP_VR_HIDE_DRIVER { get; }
    public Setting<bool> RECOMP_VR_HAND_STEERING { get; }
    public Setting<double> RECOMP_VR_RENDER_SCALE { get; }
    public Setting<bool> RECOMP_VR_FLAT_SCREEN { get; }
    public Setting<bool> RECOMP_VR_IMMERSIVE_WINDOW { get; }
    public Setting<bool> RECOMP_VR_FLAT_RACE_INTRO { get; }
    public Setting<double> RECOMP_VR_LEAN_BACK_DEGREES { get; }
    public Setting<string> RECOMP_VR_BULLET_BILL_CAMERA { get; }
    public Setting<int> RECOMP_VR_FIRST_PERSON_HIDDEN_MODEL { get; }
    public Setting<string> RECOMP_VR_FIRST_PERSON_SEAT { get; }
    public Setting<bool> RECOMP_VR_BODY_IK { get; }
    public Setting<bool> RECOMP_VR_BODY_IK_HANDS_ONLY { get; }
    public Setting<double> RECOMP_VR_BODY_IK_LEFT_HAND_ROLL { get; }
    public Setting<double> RECOMP_VR_BODY_IK_RIGHT_HAND_ROLL { get; }
    public Setting<bool> RECOMP_VR_PLACEHOLDER_STEERING_WHEEL { get; }
    public Setting<double> RECOMP_VR_WHEEL_KART_DEGREES { get; }
    public Setting<double> RECOMP_VR_WHEEL_BIKE_DEGREES { get; }
    public Setting<string> RECOMP_VR_COCKPIT_ITEM_HAND { get; }
    public Setting<bool> RECOMP_VR_COCKPIT_ITEM_THROW { get; }
    public Setting<double> RECOMP_VR_COCKPIT_ITEM_SIZE { get; }
    #endregion

    #region Public API
    public T Get<T>(Setting<T> setting) => setting.Value;

    public bool Set<T>(Setting<T> setting, T value, bool skipSave = false)
    {
        if (value == null)
            throw new ArgumentNullException(nameof(value));

        if (RecompOperationCoordinator.IsBusy && (ReferenceEquals(setting, GAME_LOCATION) || ReferenceEquals(setting, DOLPHIN_LOCATION)))
            return false;

        if (ReferenceEquals(setting, ENABLE_RECOMP) || ReferenceEquals(setting, ENABLE_RECOMP_VR))
        {
            if (RecompOperationCoordinator.IsBusy)
                return false;
            using var lease = RecompOperationCoordinator.Acquire(_applicationData.DirectoryPath, _fileSystem);
            if (value is true)
                (ReferenceEquals(setting, ENABLE_RECOMP) ? ENABLE_RECOMP_VR : ENABLE_RECOMP).Set(false, skipSave: true);
            var changed = setting.Set(value, skipSave);
            _backendSelection.Restore(Get(ENABLE_RECOMP), Get(ENABLE_RECOMP_VR));
            _recompSettingManager.ReloadSettings(_backendSelection.ConfigPath);
            return changed;
        }

        var previous = setting.Value;
        if (!setting.Set(value, skipSave))
            return false;
        if (
            !EqualityComparer<T>.Default.Equals(previous, value)
            && (ReferenceEquals(setting, USER_FOLDER_PATH) || ReferenceEquals(setting, DOLPHIN_LOCATION))
        )
            _dolphinSettingManager.ReloadSettings(_dolphinPaths.Resolve(DOLPHIN_LOCATION.Value, USER_FOLDER_PATH.Value).ConfigFolderPath);
        return true;
    }

    public bool PathsSetupCorrectly()
    {
        var reportResult = ValidateCorePathSettings();
        return reportResult.IsSuccess && reportResult.Value.IsValid;
    }

    public bool DolphinPathsSetupCorrectly()
    {
        var reportResult = ValidateDolphinPathSettings();
        return reportResult.IsSuccess && reportResult.Value.IsValid;
    }

    public OperationResult<SettingsValidationReport> ValidateCorePathSettings()
    {
        return ValidatePathSettings(requireDolphin: !IsRecompModeActive());
    }

    private OperationResult<SettingsValidationReport> ValidateDolphinPathSettings() => ValidatePathSettings(requireDolphin: true);

    public bool IsRecompModeActive() =>
        RecompPlatform.IsSupported && Get<bool>(ENABLE_RECOMP) || OperatingSystem.IsWindows() && Get<bool>(ENABLE_RECOMP_VR);

    private OperationResult<SettingsValidationReport> ValidatePathSettings(bool requireDolphin)
    {
        try
        {
            var issues = new List<SettingsValidationIssue>();

            if (requireDolphin && (string.IsNullOrWhiteSpace(Get<string>(USER_FOLDER_PATH)) || !USER_FOLDER_PATH.IsValid()))
                issues.Add(new(SettingsValidationCode.InvalidUserFolderPath, USER_FOLDER_PATH.Name, "User folder path is invalid."));

            // Sandboxed Wheel Wizard is allowed to omit the Dolphin location setting as it uses the bundled version
            if (
                requireDolphin
                && (!IsFlatpakSandboxed && string.IsNullOrWhiteSpace(Get<string>(DOLPHIN_LOCATION)) || !DOLPHIN_LOCATION.IsValid())
            )
                issues.Add(
                    new(SettingsValidationCode.InvalidDolphinLocation, DOLPHIN_LOCATION.Name, "Dolphin path or command is invalid.")
                );

            if (!GAME_LOCATION.IsValid())
                issues.Add(new(SettingsValidationCode.InvalidGameLocation, GAME_LOCATION.Name, "Game file path is invalid."));

            return Ok(new SettingsValidationReport(issues));
        }
        catch (Exception ex)
        {
            return Fail(ex);
        }
    }

    public void LoadSettings()
    {
        if (_hasLoadedSettings)
            return;

        ImportOfficialSettings();
        _whWzSettingManager.LoadSettings(WhWzConfigFilePath);
        if (Get<bool>(ENABLE_RECOMP_VR))
            ENABLE_RECOMP.Set(false, skipSave: true);
        _backendSelection.Restore(Get<bool>(ENABLE_RECOMP), Get<bool>(ENABLE_RECOMP_VR));
        _dolphinSettingManager.LoadSettings(
            _dolphinPaths.Resolve(Get<string>(DOLPHIN_LOCATION), Get<string>(USER_FOLDER_PATH)).ConfigFolderPath
        );
        _recompSettingManager.LoadSettings(_backendSelection.ConfigPath);
        _hasLoadedSettings = true;
    }

    // This launcher keeps its own preferences beside official Wheel Wizard's, which stay untouched.
    private string WhWzConfigFilePath => _fileSystem.Path.Combine(_applicationData.DirectoryPath, "config-vr.json");

    /// <summary>
    /// Seeds this launcher's preferences from official Wheel Wizard's <c>config.json</c> the first time
    /// it runs; from then on the two files are independent.
    /// </summary>
    private void ImportOfficialSettings()
    {
        var configPath = WhWzConfigFilePath;
        try
        {
            if (_fileSystem.File.Exists(configPath))
                return;
            _fileSystem.Directory.CreateDirectory(_applicationData.DirectoryPath);
            var official = _fileSystem.Path.Combine(_applicationData.DirectoryPath, "config.json");
            if (_fileSystem.File.Exists(official))
                _fileSystem.File.Copy(official, configPath, overwrite: false);
            else
            {
                using var created = _fileSystem.FileStream.New(configPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                created.Write("{}"u8);
            }
        }
        catch (IOException) when (_fileSystem.File.Exists(configPath))
        {
            // Another launcher completed the one-time import first; read its result.
        }
    }
    #endregion

    #region Registration Helpers
    private WhWzSetting<T> RegisterWhWz<T>(string name, T defaultValue, Func<T, bool>? validation = null)
    {
        var setting = new WhWzSetting<T>(name, defaultValue!, setting => _whWzSettingManager.SaveSettings(WhWzConfigFilePath, setting));
        if (validation != null)
            setting.SetValidation(validation);

        _whWzSettingManager.RegisterSetting(setting);
        _ownedSettings.Add(setting);
        return setting;
    }

    private DolphinSetting<T> RegisterDolphin<T>((string, string, string) location, T defaultValue, Func<T, bool>? validation = null)
    {
        var setting = new DolphinSetting<T>(
            location,
            defaultValue!,
            setting =>
                _dolphinSettingManager.SaveSettings(
                    _dolphinPaths.Resolve(Get<string>(DOLPHIN_LOCATION), Get<string>(USER_FOLDER_PATH)).ConfigFolderPath,
                    setting
                )
        );
        if (validation != null)
            setting.SetValidation(validation);

        _dolphinSettingManager.RegisterSetting(setting);
        _ownedSettings.Add(setting);
        return setting;
    }

    private RecompSetting<T> RegisterRecomp<T>((string, string) location, T defaultValue, Func<T, bool>? validation = null)
    {
        var setting = new RecompSetting<T>(
            location,
            defaultValue!,
            setting => _recompSettingManager.SaveSettings(_backendSelection.ConfigPath, setting)
        );
        if (validation != null)
            setting.SetValidation(validation);

        _recompSettingManager.RegisterSetting(setting);
        _ownedSettings.Add(setting);
        return setting;
    }

    private static Func<double, bool> InRange(double low, double high) => value => value >= low && value <= high;

    private static Func<string, bool> OneOf(params string[] names) => value => names.Contains(value);
    #endregion

    public void Dispose()
    {
        foreach (var setting in _ownedSettings)
        {
            setting.Changed -= _signalBus.Publish;
            if (setting is IDisposable disposable)
                disposable.Dispose();
        }
        _ownedSettings.Clear();
    }

    public ExtensionConfigurationInfo CheckExtensionConfiguration()
    {
        if (IsRecompModeActive())
        {
            if (IsMissingExtensionForRecomp)
            {
                return ExtensionConfigurationInfo.MissingRecomp;
            }
        }
        else
        {
            if (IsMissingExtensionForDolphin)
            {
                return ExtensionConfigurationInfo.MissingDolphin;
            }
        }
        return ExtensionConfigurationInfo.ExtensionFoundOrNotNeeded;
    }
}
