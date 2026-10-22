using Microsoft.Extensions.Logging;
using WheelWizard.AutoUpdating;
using WheelWizard.CustomDistributions;
using WheelWizard.Dolphin.Discovery;
using WheelWizard.Dolphin.Paths;
using WheelWizard.Recomp;
using WheelWizard.Settings;
using WheelWizard.Settings.Types;
using WheelWizard.Shared.Downloads;
using WheelWizard.Shared.Processes;
using WheelWizard.WiiManagement.MiiManagement;

namespace WheelWizard.Test.Features.Recomp;

[Collection("SettingsFeature")]
public sealed class RecompVrTests
{
    private readonly RecompVrTestContext _context = new();

    private Testably.Abstractions.Testing.MockFileSystem Fs => _context.FileSystem;

    private void Write(string path, string text) => _context.Write(path, text);

    [Fact]
    public void MiiMutationIsRejectedDuringGameLease()
    {
        var repository = new MiiRepositoryServiceService(
            Fs,
            Substitute.For<ISettingsManager>(),
            _context.Paths,
            Substitute.For<IDolphinPaths>(),
            _context.ApplicationData,
            Substitute.For<IRecompDolphinDataService>()
        );
        using var lease = RecompOperationCoordinator.Acquire(_context.Root, Fs);
        Assert.True(repository.ForceCreateDatabase().IsFailure);
        Assert.True(repository.SaveAllBlocks([]).IsFailure);
    }

    [Fact]
    public void SelfUpdaterSelectsOnlyTheVrLauncherAsset()
    {
        var updater = new WheelWizard.AutoUpdating.Platforms.WindowsUpdatePlatform(
            Fs,
            Substitute.For<IDownloadService>(),
            Substitute.For<IApplicationProcess>(),
            Substitute.For<IProcessLauncher>(),
            Substitute.For<IUpdatePresentation>()
        );
        var release = new WheelWizard.GitHub.Domain.GithubRelease
        {
            TagName = "v2.5.5",
            Assets = [new() { Name = "WheelWizardWindows.exe", BrowserDownloadUrl = "https://example.invalid/official" }],
        };
        Assert.Null(updater.GetAssetForCurrentPlatform(release));
        release.Assets.Add(new() { Name = "WheelWizardVRWindows.exe", BrowserDownloadUrl = "https://example.invalid/vr" });
        Assert.Equal("https://example.invalid/vr", updater.GetAssetForCurrentPlatform(release)!.BrowserDownloadUrl);
    }

    [Theory]
    [InlineData(true, false, RecompBackendKind.Normal)]
    [InlineData(false, true, RecompBackendKind.OpenXR)]
    [InlineData(false, false, RecompBackendKind.Dolphin)]
    [InlineData(true, true, RecompBackendKind.OpenXR)]
    public void BackendMigration(bool normal, bool vr, RecompBackendKind expected)
    {
        _context.Selection.Restore(normal, vr);
        // The VR backend is Windows-only; elsewhere the normal one runs where the recomp is supported.
        var expectedHere =
            OperatingSystem.IsWindows() ? expected
            : normal && RecompPlatform.IsSupported ? RecompBackendKind.Normal
            : RecompBackendKind.Dolphin;
        Assert.Equal(expectedHere, _context.Selection.Kind);
    }

    [Fact]
    public void SwitchesAreExclusiveAndBusyChangesAreRejected()
    {
        var settings = _context.CreateSettings();
        Assert.True(settings.Set(settings.ENABLE_RECOMP, true));
        Assert.True(settings.Set(settings.ENABLE_RECOMP_VR, true));
        Assert.False(settings.Get(settings.ENABLE_RECOMP));
        Assert.True(settings.Get(settings.ENABLE_RECOMP_VR));
        using (RecompOperationCoordinator.Acquire(_context.Root, Fs))
            Assert.False(settings.Set(settings.ENABLE_RECOMP, true));
        Assert.True(settings.Set(settings.ENABLE_RECOMP, true));
        Assert.False(settings.Get(settings.ENABLE_RECOMP_VR));
        Assert.True(settings.Set(settings.ENABLE_RECOMP, false));
        Assert.False(settings.IsRecompModeActive());
    }

    [Fact]
    public void EnvironmentsCaptureBackendAndNeverShareInstallOrCache()
    {
        if (!OperatingSystem.IsWindows())
            return;
        RecompEnvironment Create() =>
            new(
                Fs,
                _context.Paths,
                _context.ApplicationData,
                _context.Selection,
                Substitute.For<ISettingsManager>(),
                Substitute.For<ICustomDistributionPaths>()
            );
        _context.Selection.Restore(true, false);
        var normal = Create();
        _context.Selection.Restore(false, true);
        var vr = Create();
        Assert.Equal("patchzyy", normal.Backend.RepositoryOwner);
        Assert.Equal("iChris4", vr.Backend.RepositoryOwner);
        Assert.NotEqual(normal.InstallFolderPath, vr.InstallFolderPath);
        Assert.NotEqual(normal.CacheFolderPath, vr.CacheFolderPath);
        Assert.NotEqual(normal.UserDataFolderPath, vr.UserDataFolderPath);
        Assert.EndsWith(Path.Combine("Recomp", "Install"), normal.InstallFolderPath);
        Assert.EndsWith(Path.Combine("RecompVR", "Install"), vr.InstallFolderPath);
        // Both backends share the normal installation's NAND copy.
        Assert.Equal(normal.NandCopyFolderPath, vr.NandCopyFolderPath);
    }

    [Theory]
    [InlineData("wiicompiled-openxr-vr", "0.2.32", true, true)]
    [InlineData("wiicompiled", "0.2.32", true, false)]
    [InlineData("wiicompiled-openxr-vr", "0.2.31", true, false)]
    [InlineData("wiicompiled-openxr-vr", "0.2.32", false, false)]
    public void IdentityRequiresVrCapabilityAndExactVersion(string id, string version, bool capable, bool expected)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(
            new
            {
                productId = id,
                version,
                openxrD3D12 = capable,
            }
        );
        Assert.Equal(expected, RecompSetupIdentity.IsMatchingVr(json, "v0.2.32"));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("0.2.32")]
    [InlineData("null")]
    [InlineData("not json")]
    public void MissingIdentityIsNeverAcceptedAsVr(string text) => Assert.False(RecompSetupIdentity.IsMatchingVr(text, "0.2.32"));

    [Theory]
    [InlineData(RecompGame.Base, "--launch-base")]
    [InlineData(RecompGame.RetroRewind, "--launch-retro")]
    public void BothBackendsUseTheSelectedProduct(RecompGame game, string argument)
    {
        foreach (var backend in new[] { RecompBackend.Normal, RecompBackend.OpenXR })
        {
            Assert.Equal([argument], RecompSetupCommandBuilder.BuildLaunchArguments(game == RecompGame.RetroRewind));
            Assert.False(string.IsNullOrEmpty(backend.RepositoryName));
        }
    }

    [Fact]
    public void VrConfigPreservesNormalAndImportsOnlyControllerOnce()
    {
        var normal = _context.Paths.ConfigFilePath;
        var vr = RecompBackend.OpenXR.Config(_context.Root);
        const string original =
            "[video]\ngraphics_api = \"vulkan\"\n[controller]\na = \"south\"\n[paths]\ndvd_root = 'private game path'\n";
        Write(normal, original);
        Write(vr, "[paths]\ndvd_root = 'VR game path'\n[vr]\nworld_units_per_meter = 30.0\n");
        RecompConfig.PrepareVr(Fs, normal, vr);
        Assert.Equal(original, Fs.File.ReadAllText(normal));
        Assert.Equal("d3d12", RecompConfig.ReadString(Fs, vr, "video", "graphics_api"));
        Assert.Equal("VR game path", RecompConfig.ReadString(Fs, vr, "paths", "dvd_root"));
        Assert.Contains("world_units_per_meter = 30.0", Fs.File.ReadAllText(vr));
        Assert.Equal("south", RecompConfig.ReadString(Fs, vr, "controller", "a"));
        RecompConfig.Set(Fs, vr, "controller", "a", "\"east\"");
        RecompConfig.PrepareVr(Fs, normal, vr);
        Assert.Equal("east", RecompConfig.ReadString(Fs, vr, "controller", "a"));
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("enabled = true", true)]
    [InlineData("enabled = false # desktop preference", false)]
    public void VrPreparationRetainsOpenXrPreference(string entry, bool enabled)
    {
        var vr = RecompBackend.OpenXR.Config(_context.Root);
        Write(vr, "[vr]\n" + entry + "\nmirror_view = \"left\"\nfirst_person = true\n");
        RecompConfig.PrepareVr(Fs, _context.Paths.ConfigFilePath, vr);
        RecompConfig.PrepareVr(Fs, _context.Paths.ConfigFilePath, vr);
        var content = Fs.File.ReadAllText(vr);
        Assert.Contains("enabled = " + enabled.ToString().ToLowerInvariant(), content);
        Assert.Contains("required = false", content);
        Assert.Contains("first_person = true", content);
        Assert.Equal("left", RecompConfig.ReadString(Fs, vr, "vr", "mirror_view"));
    }

    [Fact]
    public void VrPreparationKeepsAnOfferedApiAndRepairsAnyOther()
    {
        // OpenXR drives the headset on either offered API, so a Vulkan choice survives a VR launch.
        // Only a value the runtime would refuse is repaired, and with OpenXR off the installation is
        // an ordinary desktop one where the player's own API always stands.
        var vr = RecompBackend.OpenXR.Config(_context.Root);
        Write(vr, "[vr]\nenabled = true\n[video]\ngraphics_api = \"vulkan\"\n");
        RecompConfig.PrepareVr(Fs, _context.Paths.ConfigFilePath, vr);
        Assert.Equal("vulkan", RecompConfig.ReadString(Fs, vr, "video", "graphics_api"));

        RecompConfig.Set(Fs, vr, "video", "graphics_api", "\"auto\"");
        RecompConfig.PrepareVr(Fs, _context.Paths.ConfigFilePath, vr);
        Assert.Equal("d3d12", RecompConfig.ReadString(Fs, vr, "video", "graphics_api"));

        RecompConfig.Set(Fs, vr, "vr", "enabled", "false");
        RecompConfig.Set(Fs, vr, "video", "graphics_api", "\"opengl\"");
        RecompConfig.PrepareVr(Fs, _context.Paths.ConfigFilePath, vr);
        Assert.Equal("opengl", RecompConfig.ReadString(Fs, vr, "video", "graphics_api"));
    }

    [Fact]
    public void VrPreferencesPersistOnlyToSelectedBackendAndReloadAfterSwitch()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var selection = _context.Selection;
        selection.Restore(false, true);
        var vr = selection.ConfigPath;
        Write(vr, "[vr]\nenabled = true\n[video]\ngraphics_api = \"d3d12\"\n");
        var manager = new RecompSettingManager(Fs);
        var settings = _context.CreateSettings(manager);
        manager.LoadSettings(selection.ConfigPath);
        settings.Set(settings.RECOMP_VR_ENABLED, false);
        settings.Set(settings.RECOMP_VR_MIRROR_VIEW, "both");
        settings.Set(settings.RECOMP_VR_FIRST_PERSON, true);
        settings.Set(settings.RECOMP_VR_FIRST_PERSON_ROTATION, "full");
        settings.Set(settings.RECOMP_VR_HIDE_DRIVER, false);
        settings.Set(settings.RECOMP_VR_HAND_STEERING, false);
        settings.Set(settings.RECOMP_VR_RENDER_SCALE, 1.25);
        var saved = Fs.File.ReadAllText(vr);
        selection.Restore(true, false);
        Write(selection.ConfigPath, "[video]\ngraphics_api = \"vulkan\"\n");
        manager.ReloadSettings(selection.ConfigPath);
        Assert.Equal("normal", settings.Get(settings.RECOMP_VR_MIRROR_VIEW));
        Assert.False(settings.Get(settings.RECOMP_VR_FIRST_PERSON));
        Assert.Equal("yaw_pitch", settings.Get(settings.RECOMP_VR_FIRST_PERSON_ROTATION));
        Assert.True(settings.Get(settings.RECOMP_VR_HAND_STEERING));
        Assert.Equal(1.0, settings.Get(settings.RECOMP_VR_RENDER_SCALE));
        Assert.Equal("[video]\ngraphics_api = \"vulkan\"\n", Fs.File.ReadAllText(selection.ConfigPath));
        selection.Restore(false, true);
        manager.ReloadSettings(selection.ConfigPath);
        Assert.False(settings.Get(settings.RECOMP_VR_ENABLED));
        Assert.Equal("both", settings.Get(settings.RECOMP_VR_MIRROR_VIEW));
        Assert.True(settings.Get(settings.RECOMP_VR_FIRST_PERSON));
        Assert.Equal("full", settings.Get(settings.RECOMP_VR_FIRST_PERSON_ROTATION));
        Assert.False(settings.Get(settings.RECOMP_VR_HIDE_DRIVER));
        Assert.False(settings.Get(settings.RECOMP_VR_HAND_STEERING));
        Assert.Equal(1.25, settings.Get(settings.RECOMP_VR_RENDER_SCALE));
        Assert.Equal(saved, Fs.File.ReadAllText(vr));
        Assert.Contains("hand_steering = false", saved);
    }

    [Fact]
    public void HandSteeringFollowsTheConfigurationAndDefaultsOnWithoutIt()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var selection = _context.Selection;
        selection.Restore(false, true);
        var vr = selection.ConfigPath;
        Write(vr, "[vr]\nenabled = true\n");
        var manager = new RecompSettingManager(Fs);
        var settings = _context.CreateSettings(manager);
        manager.LoadSettings(selection.ConfigPath);
        // The runtime's own default (kVrHandSteeringDefault) is on, and loading writes nothing back.
        Assert.True(settings.Get(settings.RECOMP_VR_HAND_STEERING));
        Assert.Equal("[vr]\nenabled = true\n", Fs.File.ReadAllText(vr));
        Write(vr, "[vr]\nenabled = true\nhand_steering = false\n");
        manager.ReloadSettings(selection.ConfigPath);
        Assert.False(settings.Get(settings.RECOMP_VR_HAND_STEERING));
        settings.Set(settings.RECOMP_VR_HAND_STEERING, true);
        // Written with the platform's line endings.
        Assert.Equal("[vr]\nenabled = true\nhand_steering = true\n", Fs.File.ReadAllText(vr).ReplaceLineEndings("\n"));
    }

    [Fact]
    public void CameraSettingsReadAsTheRuntimeDoes()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var selection = _context.Selection;
        selection.Restore(false, true);
        var vr = selection.ConfigPath;
        // runtime_config.h discards a value outside its range or names, and an integer key written
        // as a float, and falls back to its default: the launcher must show that same default.
        Write(
            vr,
            "[vr]\nenabled = true\nflat_screen = true\nlean_back_degrees = 60.0\nbullet_bill_camera = \"sideways\"\n"
                + "first_person_hidden_model = -1.0\nfirst_person_seat = \"custom\"\nwheel_kart_degrees = 120\n"
                + "cockpit_item_hand = \"off\"\ncockpit_item_size = 0.3\n"
        );
        var manager = new RecompSettingManager(Fs);
        var settings = _context.CreateSettings(manager);
        manager.LoadSettings(selection.ConfigPath);
        Assert.True(settings.Get(settings.RECOMP_VR_FLAT_SCREEN));
        Assert.False(settings.Get(settings.RECOMP_VR_IMMERSIVE_WINDOW));
        Assert.True(settings.Get(settings.RECOMP_VR_FLAT_RACE_INTRO));
        Assert.Equal(0.0, settings.Get(settings.RECOMP_VR_LEAN_BACK_DEGREES));
        Assert.Equal("inside", settings.Get(settings.RECOMP_VR_BULLET_BILL_CAMERA));
        Assert.Equal(0, settings.Get(settings.RECOMP_VR_FIRST_PERSON_HIDDEN_MODEL));
        Assert.Equal("custom", settings.Get(settings.RECOMP_VR_FIRST_PERSON_SEAT));
        Assert.True(settings.Get(settings.RECOMP_VR_BODY_IK));
        Assert.False(settings.Get(settings.RECOMP_VR_BODY_IK_HANDS_ONLY));
        Assert.Equal(90.0, settings.Get(settings.RECOMP_VR_BODY_IK_LEFT_HAND_ROLL));
        Assert.Equal(-90.0, settings.Get(settings.RECOMP_VR_BODY_IK_RIGHT_HAND_ROLL));
        Assert.False(settings.Get(settings.RECOMP_VR_PLACEHOLDER_STEERING_WHEEL));
        Assert.Equal(120.0, settings.Get(settings.RECOMP_VR_WHEEL_KART_DEGREES));
        Assert.Equal(45.0, settings.Get(settings.RECOMP_VR_WHEEL_BIKE_DEGREES));
        Assert.Equal("off", settings.Get(settings.RECOMP_VR_COCKPIT_ITEM_HAND));
        Assert.True(settings.Get(settings.RECOMP_VR_COCKPIT_ITEM_THROW));
        Assert.Equal(0.3, settings.Get(settings.RECOMP_VR_COCKPIT_ITEM_SIZE));

        // Written as the runtime's own writer would: the model a whole TOML integer.
        settings.Set(settings.RECOMP_VR_FIRST_PERSON_HIDDEN_MODEL, -1);
        settings.Set(settings.RECOMP_VR_LEAN_BACK_DEGREES, -12.0);
        settings.Set(settings.RECOMP_VR_COCKPIT_ITEM_SIZE, 0.28);
        Assert.False(settings.Set(settings.RECOMP_VR_WHEEL_BIKE_DEGREES, 200.0));
        var saved = Fs.File.ReadAllText(vr);
        Assert.Contains("first_person_hidden_model = -1" + Environment.NewLine, saved);
        Assert.Contains("lean_back_degrees = -12.0", saved);
        Assert.Contains("cockpit_item_size = 0.28", saved);
        Assert.DoesNotContain("wheel_bike_degrees", saved);
        manager.ReloadSettings(selection.ConfigPath);
        Assert.Equal(-1, settings.Get(settings.RECOMP_VR_FIRST_PERSON_HIDDEN_MODEL));
    }

    [Fact]
    public void ResettingVrSettingsRemovesOnlyTheirKeysWithTheirComments()
    {
        var vr = RecompBackend.OpenXR.Config(_context.Root);
        Write(
            vr,
            "[vr]\nenabled = false\ncontroller_mode = \"gamepad\"\n# Race view\n# Live.\nflat_screen = true\n"
                + "first_person_seat = \"custom\"\n# Kept\nworld_units_per_meter = 400.0\nfirst_person = true\n\n"
                + "[video]\nrender_scale = 2.0\n"
        );
        var manager = new RecompSettingManager(Fs);
        manager.RemoveTomlSettings(vr, "vr", RecompVrConfig.PreferenceKeys);
        // As the runtime's toml11 writer removes a key's comments with it; other sections keep theirs.
        Assert.Equal(
            "[vr]\nenabled = false\ncontroller_mode = \"gamepad\"\n# Kept\nworld_units_per_meter = 400.0\n\n[video]\nrender_scale = 2.0\n",
            Fs.File.ReadAllText(vr).ReplaceLineEndings("\n")
        );
    }

    [Fact]
    public void SwitchingConfigsResetsMissingSettingsWithoutWritingDefaults()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var selection = _context.Selection;
        selection.Restore(true, false);
        Write(selection.ConfigPath, "[video]\nresolution_multiplier = 3.0\n");
        var manager = new RecompSettingManager(Fs);
        var setting = new RecompSetting<double>(
            ("video", "resolution_multiplier"),
            1.0,
            invoking => manager.SaveSettings(selection.ConfigPath, invoking)
        );
        manager.RegisterSetting(setting);
        manager.LoadSettings(selection.ConfigPath);
        Assert.Equal(3.0, setting.Get());
        selection.Restore(false, true);
        Write(selection.ConfigPath, "[video]\n");
        manager.ReloadSettings(selection.ConfigPath);
        Assert.Equal(1.0, setting.Get());
        Assert.Equal("[video]\n", Fs.File.ReadAllText(selection.ConfigPath));
    }

    [Fact]
    public void PreferencesImportOnceWithoutChangingOfficialConfiguration()
    {
        var official = Fs.Path.Combine(_context.Root, "config.json");
        Write(official, "{\"ForceWiimote\":true}");
        var settings = _context.CreateSettings(whWzSettings: new WhWzSettingManager(Substitute.For<ILogger<WhWzSettingManager>>(), Fs));
        settings.LoadSettings();
        Assert.True(settings.Get(settings.FORCE_WIIMOTE));
        Assert.True(settings.Set(settings.FORCE_WIIMOTE, false));
        Assert.Contains("true", Fs.File.ReadAllText(official));
        Assert.Contains("false", Fs.File.ReadAllText(Fs.Path.Combine(_context.Root, "config-vr.json")));
    }

    [Fact]
    public void SharedNandHonorsNormalCustomPathWithoutRewritingNormalConfig()
    {
        var normal = _context.Paths.ConfigFilePath;
        var vr = RecompBackend.OpenXR.Config(_context.Root);
        var nand = Fs.Path.GetFullPath("/vr-tests/My # NAND");
        Fs.Directory.CreateDirectory(nand);
        var original = "[paths]\nnand_root = " + RecompConfig.Quote(nand) + " # keep comment\n";
        Write(normal, original);
        Write(vr, "[paths]\n");
        var data = new RecompDolphinDataService(
            Substitute.For<ISettingsManager>(),
            Substitute.For<IRecompSettingManager>(),
            Fs,
            Substitute.For<IDolphinDiscoveryService>(),
            _context.Paths,
            _context.ApplicationData,
            _context.Selection
        );
        Assert.True(data.ApplyNandToRecompConfig().IsSuccess);
        Assert.Equal(original, Fs.File.ReadAllText(normal));
        var stored = RecompConfig.ReadString(Fs, vr, "paths", "nand_root")!;
        Assert.Equal(nand, Fs.Path.GetFullPath(stored, Fs.Path.GetDirectoryName(vr)!));
    }

    [Fact]
    public void SharedLeaseRejectsOverlappingOperationsAndReleasesAfterFailure()
    {
        using (RecompOperationCoordinator.Acquire(_context.Root, Fs))
            Assert.Throws<IOException>(() => RecompOperationCoordinator.Acquire(_context.Root, Fs));
        using var next = RecompOperationCoordinator.Acquire(_context.Root, Fs);
        Assert.True(RecompOperationCoordinator.IsBusy);
    }

    [Fact]
    public void RealFileLeaseRejectsAnotherOwnerAndRecoversAfterHandleClose()
    {
        var root = Path.Combine(Path.GetTempPath(), "WheelWizard-VR-lease-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using (
                var external = new FileStream(
                    Path.Combine(root, ".wiicompiled-shared-operation.lock"),
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None
                )
            )
                Assert.Throws<IOException>(() => RecompOperationCoordinator.Acquire(root));
            using var recovered = RecompOperationCoordinator.Acquire(root);
            Assert.True(RecompOperationCoordinator.IsBusy);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
