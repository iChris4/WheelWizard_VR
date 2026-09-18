using Microsoft.Extensions.Logging;
using Testably.Abstractions.Testing;
using WheelWizard.Recomp;
using WheelWizard.Recomp.Domain;
using WheelWizard.Services;
using WheelWizard.Settings;
using WheelWizard.Settings.Types;
using WheelWizard.Test.Features.Settings;

namespace WheelWizard.Test.Features.Recomp;

[Collection("SettingsFeature")]
public sealed class RecompVrTests : IDisposable
{
    private readonly MockFileSystem _fs = new();

    public RecompVrTests() => SettingsTestUtils.InitializeSettingsRuntime(Path.GetFullPath("VrTests/Dolphin"));

    public void Dispose()
    {
        SettingsTestUtils.ResetSettingsRuntime();
        SettingsTestUtils.ResetSignalRuntime();
    }

    private void Write(string path, string text)
    {
        _fs.Directory.CreateDirectory(_fs.Path.GetDirectoryName(path)!);
        _fs.File.WriteAllText(path, text);
    }

    [Fact]
    public void MiiMutationIsRejectedDuringGameLease()
    {
        var repository = new WheelWizard.WiiManagement.MiiManagement.MiiRepositoryServiceService(
            _fs,
            Substitute.For<ISettingsManager>(),
            Substitute.For<IRecompDolphinDataService>()
        );
        using var lease = RecompOperationCoordinator.Acquire(PathManager.WheelWizardAppdataPath, _fs);
        Assert.True(repository.ForceCreateDatabase().IsFailure);
        Assert.True(repository.SaveAllBlocks([]).IsFailure);
    }

    [Fact]
    public void SelfUpdaterSelectsOnlyTheVrLauncherAsset()
    {
        var updater = new WheelWizard.AutoUpdating.Platforms.WindowsUpdatePlatform(_fs);
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
        var selection = new RecompBackendSelection();
        selection.Restore(normal, vr);
        Assert.Equal(OperatingSystem.IsWindows() ? expected : RecompBackendKind.Dolphin, selection.Kind);
    }

    [Fact]
    public void SwitchesAreExclusiveAndBusyChangesAreRejected()
    {
        var selection = new RecompBackendSelection();
        var settings = new SettingsManager(
            Substitute.For<IWhWzSettingManager>(),
            Substitute.For<IDolphinSettingManager>(),
            Substitute.For<IRecompSettingManager>(),
            _fs,
            selection
        );
        Assert.True(settings.Set(settings.ENABLE_RECOMP, true));
        Assert.True(settings.Set(settings.ENABLE_RECOMP_VR, true));
        Assert.False(settings.Get<bool>(settings.ENABLE_RECOMP));
        Assert.True(settings.Get<bool>(settings.ENABLE_RECOMP_VR));
        using (RecompOperationCoordinator.Acquire(PathManager.WheelWizardAppdataPath, _fs))
            Assert.False(settings.Set(settings.ENABLE_RECOMP, true));
        Assert.True(settings.Set(settings.ENABLE_RECOMP, true));
        Assert.False(settings.Get<bool>(settings.ENABLE_RECOMP_VR));
        Assert.True(settings.Set(settings.ENABLE_RECOMP, false));
        Assert.False(settings.IsRecompModeActive());
    }

    [Fact]
    public void EnvironmentsCaptureBackendAndNeverShareInstallOrCache()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var settings = SettingsTestUtils.InitializeSettingsRuntime(Path.GetFullPath("VrTests/Dolphin"));
        var load = new WhWzSetting(typeof(string), "LoadPath", Path.GetFullPath("VrTests/Dolphin/Load"));
        settings.LOAD_PATH.Returns(load);
        settings.Get<string>(load).Returns((string)load.Get());
        var selection = new RecompBackendSelection();
        selection.Restore(true, false);
        var normal = new RecompEnvironment(_fs, selection);
        selection.Restore(false, true);
        var vr = new RecompEnvironment(_fs, selection);
        Assert.Equal("patchzyy", normal.Backend.RepositoryOwner);
        Assert.Equal("iChris4", vr.Backend.RepositoryOwner);
        Assert.NotEqual(normal.InstallFolderPath, vr.InstallFolderPath);
        Assert.NotEqual(normal.CacheFolderPath, vr.CacheFolderPath);
        Assert.NotEqual(normal.UserDataFolderPath, vr.UserDataFolderPath);
        Assert.EndsWith(Path.Combine("Recomp", "Install"), normal.InstallFolderPath);
        Assert.EndsWith(Path.Combine("RecompVR", "Install"), vr.InstallFolderPath);
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
            Assert.Equal(argument, RecompSetupCommandBuilder.BuildLaunchArguments(game == RecompGame.RetroRewind));
            Assert.False(string.IsNullOrEmpty(backend.RepositoryName));
        }
    }

    [Fact]
    public void VrConfigPreservesNormalAndImportsOnlyControllerOnce()
    {
        var normal = PathManager.RecompConfigFilePath;
        var vr = RecompBackend.OpenXR.Config(PathManager.WheelWizardAppdataPath);
        const string original =
            "[video]\ngraphics_api = \"vulkan\"\n[controller]\na = \"south\"\n[paths]\ndvd_root = 'private game path'\n";
        Write(normal, original);
        Write(vr, "[paths]\ndvd_root = 'VR game path'\n[vr]\nworld_units_per_meter = 30.0\n");
        RecompConfig.PrepareVr(_fs, normal, vr);
        Assert.Equal(original, _fs.File.ReadAllText(normal));
        Assert.Equal("d3d12", RecompConfig.ReadString(_fs, vr, "video", "graphics_api"));
        Assert.Equal("VR game path", RecompConfig.ReadString(_fs, vr, "paths", "dvd_root"));
        Assert.Contains("world_units_per_meter = 30.0", _fs.File.ReadAllText(vr));
        Assert.Equal("south", RecompConfig.ReadString(_fs, vr, "controller", "a"));
        RecompConfig.Set(_fs, vr, "controller", "a", "\"east\"");
        RecompConfig.PrepareVr(_fs, normal, vr);
        Assert.Equal("east", RecompConfig.ReadString(_fs, vr, "controller", "a"));
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("enabled = true", true)]
    [InlineData("enabled = false # desktop preference", false)]
    public void VrPreparationRetainsOpenXrPreference(string entry, bool enabled)
    {
        var vr = RecompBackend.OpenXR.Config(PathManager.WheelWizardAppdataPath);
        Write(vr, "[vr]\n" + entry + "\nmirror_view = \"left\"\nfirst_person = true\n");
        RecompConfig.PrepareVr(_fs, PathManager.RecompConfigFilePath, vr);
        RecompConfig.PrepareVr(_fs, PathManager.RecompConfigFilePath, vr);
        var content = _fs.File.ReadAllText(vr);
        Assert.Contains("enabled = " + enabled.ToString().ToLowerInvariant(), content);
        Assert.Contains("required = false", content);
        Assert.Contains("first_person = true", content);
        Assert.Equal("left", RecompConfig.ReadString(_fs, vr, "vr", "mirror_view"));
    }

    [Fact]
    public void VrPreparationOnlyForcesD3D12WhileOpenXrIsOn()
    {
        // OpenXR starts on D3D12 only, so a VR launch takes it whatever was chosen. With OpenXR off
        // the installation is an ordinary desktop one, and the player's own API has to survive.
        var vr = RecompBackend.OpenXR.Config(PathManager.WheelWizardAppdataPath);
        Write(vr, "[vr]\nenabled = true\n[video]\ngraphics_api = \"vulkan\"\n");
        RecompConfig.PrepareVr(_fs, PathManager.RecompConfigFilePath, vr);
        Assert.Equal("d3d12", RecompConfig.ReadString(_fs, vr, "video", "graphics_api"));

        RecompConfig.Set(_fs, vr, "vr", "enabled", "false");
        RecompConfig.Set(_fs, vr, "video", "graphics_api", "\"vulkan\"");
        RecompConfig.PrepareVr(_fs, PathManager.RecompConfigFilePath, vr);
        Assert.Equal("vulkan", RecompConfig.ReadString(_fs, vr, "video", "graphics_api"));
    }

    [Fact]
    public void VrPreferencesPersistOnlyToSelectedBackendAndReloadAfterSwitch()
    {
        var selection = new RecompBackendSelection();
        selection.Restore(false, true);
        var vr = selection.ConfigPath;
        Write(vr, "[vr]\nenabled = true\n[video]\ngraphics_api = \"d3d12\"\n");
        var manager = new RecompSettingManager(_fs, selection);
        var settings = new SettingsManager(
            Substitute.For<IWhWzSettingManager>(),
            Substitute.For<IDolphinSettingManager>(),
            manager,
            _fs,
            selection
        );
        manager.LoadSettings();
        settings.Set(settings.RECOMP_VR_ENABLED, false);
        settings.Set(settings.RECOMP_VR_MIRROR_VIEW, "both");
        settings.Set(settings.RECOMP_VR_FIRST_PERSON, true);
        settings.Set(settings.RECOMP_VR_FIRST_PERSON_ROTATION, "yaw_pitch");
        settings.Set(settings.RECOMP_VR_HIDE_DRIVER, false);
        settings.Set(settings.RECOMP_VR_RENDER_SCALE, 1.25);
        var saved = _fs.File.ReadAllText(vr);
        selection.Restore(true, false);
        Write(selection.ConfigPath, "[video]\ngraphics_api = \"vulkan\"\n");
        manager.ReloadSettings();
        Assert.Equal("normal", settings.Get<string>(settings.RECOMP_VR_MIRROR_VIEW));
        Assert.False(settings.Get<bool>(settings.RECOMP_VR_FIRST_PERSON));
        Assert.Equal(1.0, settings.Get<double>(settings.RECOMP_VR_RENDER_SCALE));
        Assert.Equal("[video]\ngraphics_api = \"vulkan\"\n", _fs.File.ReadAllText(selection.ConfigPath));
        selection.Restore(false, true);
        manager.ReloadSettings();
        Assert.False(settings.Get<bool>(settings.RECOMP_VR_ENABLED));
        Assert.Equal("both", settings.Get<string>(settings.RECOMP_VR_MIRROR_VIEW));
        Assert.True(settings.Get<bool>(settings.RECOMP_VR_FIRST_PERSON));
        Assert.Equal("yaw_pitch", settings.Get<string>(settings.RECOMP_VR_FIRST_PERSON_ROTATION));
        Assert.False(settings.Get<bool>(settings.RECOMP_VR_HIDE_DRIVER));
        Assert.Equal(1.25, settings.Get<double>(settings.RECOMP_VR_RENDER_SCALE));
        Assert.Equal(saved, _fs.File.ReadAllText(vr));
    }

    [Fact]
    public void SwitchingConfigsResetsMissingSettingsWithoutWritingDefaults()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var selection = new RecompBackendSelection();
        selection.Restore(true, false);
        Write(selection.ConfigPath, "[video]\nresolution_multiplier = 3.0\n");
        var manager = new RecompSettingManager(_fs, selection);
        var setting = new RecompSetting(typeof(double), ("video", "resolution_multiplier"), 1.0, manager.SaveSettings);
        manager.RegisterSetting(setting);
        manager.LoadSettings();
        Assert.Equal(3.0, setting.Get());
        selection.Restore(false, true);
        Write(selection.ConfigPath, "[video]\n");
        manager.ReloadSettings();
        Assert.Equal(1.0, setting.Get());
        Assert.Equal("[video]\n", _fs.File.ReadAllText(selection.ConfigPath));
    }

    [Fact]
    public void PreferencesImportOnceWithoutChangingOfficialConfiguration()
    {
        Write(PathManager.LegacyWheelWizardConfigFilePath, "{\"EnableRecomp\":true}");
        var manager = new WhWzSettingManager(Substitute.For<ILogger<WhWzSettingManager>>(), _fs);
        var setting = new WhWzSetting(typeof(bool), "EnableRecomp", false, manager.SaveSettings);
        manager.RegisterSetting(setting);
        manager.LoadSettings();
        Assert.True((bool)setting.Get());
        setting.Set(false);
        Assert.Contains("true", _fs.File.ReadAllText(PathManager.LegacyWheelWizardConfigFilePath));
        Assert.Contains("false", _fs.File.ReadAllText(PathManager.WheelWizardConfigFilePath));
    }

    [Fact]
    public void SharedNandHonorsNormalCustomPathWithoutRewritingNormalConfig()
    {
        var settings = SettingsTestUtils.InitializeSettingsRuntime(Path.GetFullPath("VrTests/Dolphin"));
        var normal = PathManager.RecompConfigFilePath;
        var vr = RecompBackend.OpenXR.Config(PathManager.WheelWizardAppdataPath);
        var nand = Path.GetFullPath("VrTests/My # NAND");
        _fs.Directory.CreateDirectory(nand);
        var original = "[paths]\nnand_root = " + RecompConfig.Quote(nand) + " # keep comment\n";
        Write(normal, original);
        Write(vr, "[paths]\n");
        var data = new RecompDolphinDataService(settings, Substitute.For<IRecompSettingManager>(), _fs);
        Assert.True(data.ApplyNandToRecompConfig().IsSuccess);
        Assert.Equal(original, _fs.File.ReadAllText(normal));
        var stored = RecompConfig.ReadString(_fs, vr, "paths", "nand_root")!;
        Assert.Equal(nand, Path.GetFullPath(stored, Path.GetDirectoryName(vr)!));
    }

    [Fact]
    public void SharedLeaseRejectsOverlappingOperationsAndReleasesAfterFailure()
    {
        using (RecompOperationCoordinator.Acquire(PathManager.WheelWizardAppdataPath, _fs))
            Assert.Throws<IOException>(() => RecompOperationCoordinator.Acquire(PathManager.WheelWizardAppdataPath, _fs));
        using var next = RecompOperationCoordinator.Acquire(PathManager.WheelWizardAppdataPath, _fs);
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
