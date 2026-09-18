using WheelWizard.Models.Enums;
using WheelWizard.Recomp;
using WheelWizard.Recomp.Domain;

namespace WheelWizard.Test.Features.Recomp;

/// <summary>
/// A deliberately small smoke suite over the three contracts that break silently when the recomp is
/// updated: the command line we hand the setup executable, the report we read back, and the status
/// that report maps onto. Everything here is string in / value out, so it runs the same everywhere.
/// </summary>
public class RecompTests
{
    [Fact]
    public void SilentInstall_BuildsTheCommandLineTheSetupExecutableExpects()
    {
        var arguments = RecompSetupCommandBuilder.BuildSilentInstallArguments(
            new()
            {
                GameFilePath = @"D:\Games\Mario Kart Wii.rvz",
                InstallFolderPath = @"D:\WheelWizard\Recomp\Install",
                RetroRewindFolderPath = @"D:\WheelWizard\RetroRewind6",
                Portable = true,
            }
        );

        Assert.Equal(
            "--silent --game \"D:\\Games\\Mario Kart Wii.rvz\" --install-dir \"D:\\WheelWizard\\Recomp\\Install\" --portable "
                + "--progress-json --retro-dir \"D:\\WheelWizard\\RetroRewind6\" --download-retro-wfc-payload",
            arguments
        );
    }

    [Fact]
    public void SilentInstall_SkipsThePayloadOnlyWhenAskedTo()
    {
        var arguments = RecompSetupCommandBuilder.BuildSilentInstallArguments(
            new()
            {
                GameFilePath = @"D:\Games\Mario Kart Wii.rvz",
                InstallFolderPath = @"D:\WheelWizard\Recomp\Install",
                RetroRewindFolderPath = @"D:\WheelWizard\RetroRewind6",
                RetroWfcPayloadMode = RecompRetroWfcPayloadMode.Skip,
            }
        );

        Assert.EndsWith("--retro-dir \"D:\\WheelWizard\\RetroRewind6\" --skip-retro-wfc-payload", arguments);
        Assert.DoesNotContain("--download-retro-wfc-payload", arguments);
    }

    [Fact]
    public void RepairProducts_PassesExactlyOnePayloadOption()
    {
        Assert.Equal(
            "--repair-products --install-dir \"D:\\Recomp\" --retro-dir \"D:\\RetroRewind6\" --download-retro-wfc-payload --progress-json",
            RecompSetupCommandBuilder.BuildRepairProductsArguments(@"D:\Recomp", @"D:\RetroRewind6")
        );
        Assert.Equal(
            "--repair-products --install-dir \"D:\\Recomp\" --retro-dir \"D:\\RetroRewind6\" --skip-retro-wfc-payload --progress-json",
            RecompSetupCommandBuilder.BuildRepairProductsArguments(@"D:\Recomp", @"D:\RetroRewind6", RecompRetroWfcPayloadMode.Skip)
        );
    }

    [Fact]
    public void RenderScaleList_KeepsWhateverTheInstallationAlreadyHolds()
    {
        // The offered scales are a short list, but the in-game panel takes any number, so a value it
        // set has to join the list rather than be shown as the nearest one.
        Assert.Equal(RecompVideoConfig.RenderScales, RecompVideoConfig.RenderScalesIncluding(1.0));
        Assert.Equal(RecompVideoConfig.RenderScales, RecompVideoConfig.RenderScalesIncluding(double.NaN));
        Assert.Equal(RecompVideoConfig.RenderScales, RecompVideoConfig.RenderScalesIncluding(4.0));

        var withOwn = RecompVideoConfig.RenderScalesIncluding(0.85);
        Assert.Equal(RecompVideoConfig.RenderScales.Count + 1, withOwn.Count);
        Assert.Contains(0.85, withOwn);
        Assert.Equal(withOwn.OrderBy(scale => scale), withOwn);
        Assert.Equal(0.85, withOwn[RecompVideoConfig.FindClosestRenderScaleIndex(withOwn, 0.85)]);

        // Picking from the plain list still lands on the nearest offered scale.
        Assert.Equal(
            0.75,
            RecompVideoConfig.RenderScales[RecompVideoConfig.FindClosestRenderScaleIndex(RecompVideoConfig.RenderScales, 0.8)]
        );
        Assert.Equal("1.00x (default)", RecompVideoConfig.DescribeRenderScale(1.0));
        Assert.Equal("0.25x", RecompVideoConfig.DescribeRenderScale(0.25));
    }

    [Fact]
    public void QuestBuild_PassesGameFilesOnlyWhenAskedTo()
    {
        Assert.Equal(
            "--build-quest --install-dir \"D:\\RecompVR\\Install\" --quest-apk \"D:\\Quest App.apk\" --output \"D:\\Out\\MarioKartWii.wcgame\" "
                + "--quest-product base --include-game-files --progress-json",
            RecompSetupCommandBuilder.BuildQuestArguments(@"D:\RecompVR\Install\", @"D:\Quest App.apk", @"D:\Out\MarioKartWii.wcgame", true)
        );
        Assert.DoesNotContain(
            "--include-game-files",
            RecompSetupCommandBuilder.BuildQuestArguments(@"D:\RecompVR\Install", @"D:\app.apk", @"D:\game.wcgame", false)
        );
    }

    [Fact]
    public void QuestBuild_NamesTheGameAndCarriesTheRetroRewindPack()
    {
        var retro = RecompSetupCommandBuilder.BuildQuestArguments(
            @"D:\RecompVR\Install",
            @"D:\app.apk",
            @"D:\RetroRewind.wcgame",
            includeGameFiles: false,
            RecompGame.RetroRewind,
            @"D:\Content\RetroRewind6"
        );
        Assert.Contains("--quest-product retro_rewind", retro);
        Assert.Contains("--retro-dir \"D:\\Content\\RetroRewind6\" --include-mod-content", retro);

        // The unmodded game has no pack to carry.
        Assert.Throws<ArgumentException>(
            () =>
                RecompSetupCommandBuilder.BuildQuestArguments(
                    @"D:\i",
                    @"D:\a.apk",
                    @"D:\g.wcgame",
                    false,
                    RecompGame.Base,
                    @"D:\RetroRewind6"
                )
        );
    }

    [Fact]
    public void QuestPackageLine_IsOnlyAnEventWhenComplete()
    {
        var package = Assert.IsType<RecompQuestPackageEvent>(
            RecompSetupOutputParser.Parse(
                """{"type":"quest-package","path":"D:\\Out\\MarioKartWii.wcgame","kitFingerprint":"28fb","includesGameFiles":true,"sizeBytes":2736026585}"""
            )
        );
        Assert.Equal(@"D:\Out\MarioKartWii.wcgame", package.Path);
        Assert.Equal("28fb", package.KitFingerprint);
        Assert.True(package.IncludesGameFiles);
        Assert.Equal(2736026585L, package.SizeBytes);

        Assert.Null(
            RecompSetupOutputParser.Parse("""{"type":"quest-package","kitFingerprint":"28fb","includesGameFiles":true,"sizeBytes":1}""")
        );
        Assert.Null(
            RecompSetupOutputParser.Parse("""{"type":"quest-package","path":"D:\\a.wcgame","kitFingerprint":"28fb","sizeBytes":1}""")
        );
    }

    [Fact]
    public void QuestBuildCapability_IsReadFromInfoJson()
    {
        Assert.True(
            RecompSetupIdentity.SupportsQuestBuild(
                """{"productId":"wiicompiled-openxr-vr","version":"0.3.0","openxrD3D12":true,"questBuild":true}"""
            )
        );
        Assert.False(
            RecompSetupIdentity.SupportsQuestBuild("""{"productId":"wiicompiled-openxr-vr","version":"0.2.38","openxrD3D12":true}""")
        );
        Assert.False(RecompSetupIdentity.SupportsQuestBuild("0.3.0"));
    }

    [Fact]
    public void InstallState_ReadsThePayloadModeTheSetupHostWrites()
    {
        var state = System.Text.Json.JsonSerializer.Deserialize<RecompInstallState>(
            """{"SchemaVersion":1,"SetupVersion":"0.3.0","InstallDir":"D:\\Recomp","RetroWfcPayloadMode":"skipped"}""",
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }
        );

        Assert.NotNull(state);
        Assert.True(state.IsRetroWfcPayloadSkipped);
    }

    [Fact]
    public void PayloadPolicy_LegacyStateWithoutModeKeepsDownloadingWhenTheServiceIsDown()
    {
        // Written by a host that predates the mode field: Retro Rewind is installed, so the host owns a
        // verified payload copy and must be asked to download, never to skip or to bother the user.
        var legacy = new RecompInstallState
        {
            SchemaVersion = 1,
            SetupVersion = "0.2.25",
            InstallDir = @"D:\Recomp",
            RetroRewindInstalled = true,
        };

        Assert.False(RecompRetroWfcPayloadPolicy.NeedsServiceProbe(legacy, hasRetroRewindSource: true));
        Assert.Equal(RecompRetroWfcPayloadDecision.Download, RecompRetroWfcPayloadPolicy.Decide(legacy, true, serviceReachable: false));
    }

    [Fact]
    public void PayloadPolicy_OnlyAFreshRetroRewindBuildAsksTheUser()
    {
        var downloaded = new RecompInstallState { RetroWfcPayloadMode = "downloaded", RetroRewindInstalled = true };
        var skipped = new RecompInstallState { RetroWfcPayloadMode = "skipped", RetroRewindInstalled = true };
        var baseOnly = new RecompInstallState { RetroWfcPayloadMode = "", RetroRewindInstalled = false };

        // Nothing to decide without a Retro Rewind source, and never a probe for a payload-bearing install.
        Assert.False(RecompRetroWfcPayloadPolicy.NeedsServiceProbe(null, hasRetroRewindSource: false));
        Assert.False(RecompRetroWfcPayloadPolicy.NeedsServiceProbe(downloaded, hasRetroRewindSource: true));
        Assert.Equal(RecompRetroWfcPayloadDecision.Download, RecompRetroWfcPayloadPolicy.Decide(downloaded, true, serviceReachable: false));

        // A skipped install stays offline while the service is down and upgrades when it is back.
        Assert.True(RecompRetroWfcPayloadPolicy.NeedsServiceProbe(skipped, hasRetroRewindSource: true));
        Assert.Equal(RecompRetroWfcPayloadDecision.Skip, RecompRetroWfcPayloadPolicy.Decide(skipped, true, serviceReachable: false));
        Assert.Equal(RecompRetroWfcPayloadDecision.Download, RecompRetroWfcPayloadPolicy.Decide(skipped, true, serviceReachable: true));

        // A fresh install and a base-only install both need a brand new Retro Rewind build.
        Assert.Equal(RecompRetroWfcPayloadDecision.AskUser, RecompRetroWfcPayloadPolicy.Decide(null, true, serviceReachable: false));
        Assert.Equal(RecompRetroWfcPayloadDecision.AskUser, RecompRetroWfcPayloadPolicy.Decide(baseOnly, true, serviceReachable: false));
        Assert.Equal(RecompRetroWfcPayloadDecision.Download, RecompRetroWfcPayloadPolicy.Decide(null, true, serviceReachable: true));
    }

    [Fact]
    public void ProductsLine_IsReadBackAsTheProductStateItReports()
    {
        var parsed = RecompSetupOutputParser.Parse(
            """
            {"type":"products","setupVersion":"0.3.0","installDir":"D:\\WiiCompiled","rebuildRequired":false,"base":{"status":"current","detail":"ok"},"retroRewind":{"status":"code-pul-changed","detail":"Code.pul changed"}}
            """
        );

        var products = Assert.IsType<RecompProductsEvent>(parsed);
        Assert.Equal("0.3.0", products.SetupVersion);
        Assert.True(products.Base.IsCurrent);
        Assert.Equal(RecompProductState.CodePulChanged, products.RetroRewind.State);
        Assert.True(products.ActionRequired);
    }

    [Fact]
    public void Status_IsOnlyReadyWhenTheInstallWasActuallyVerified()
    {
        var current = new RecompProductStatus(RecompProductState.Current, "ok");
        var verified = new RecompProductsEvent("0.3.0", @"D:\WiiCompiled", RebuildRequired: false, current, current);

        Assert.Equal(WheelWizardStatus.Ready, RecompStatusResolver.Resolve(true, "0.3.0", "v0.3.0", verified));
        Assert.Equal(WheelWizardStatus.OutOfDate, RecompStatusResolver.Resolve(true, "0.3.0", "v0.4.0", verified));
        Assert.Equal(WheelWizardStatus.NotInstalled, RecompStatusResolver.Resolve(true, null, "v0.3.0", verified));
        Assert.Equal(WheelWizardStatus.ConfigNotFinished, RecompStatusResolver.Resolve(false, "0.3.0", "v0.3.0", verified));

        // A check that never answered must never read as ready.
        Assert.Equal(WheelWizardStatus.OutOfDate, RecompStatusResolver.Resolve(true, "0.3.0", "v0.3.0", products: null));
    }
}
