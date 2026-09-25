using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Testably.Abstractions.Testing;
using WheelWizard.GitHub;
using WheelWizard.GitHub.Domain;
using WheelWizard.Recomp;
using WheelWizard.Recomp.Domain;
using WheelWizard.Services;
using WheelWizard.Test.Features.Settings;

namespace WheelWizard.Test.Features.Recomp;

[Collection("SettingsFeature")]
public sealed class RecompBackendServiceTests : IDisposable
{
    private readonly MockFileSystem _fs = new();
    private readonly IRecompProcessRunner _runner = Substitute.For<IRecompProcessRunner>();
    private readonly IGitHubSingletonService _github = Substitute.For<IGitHubSingletonService>();
    private readonly IRecompSetupDownloader _downloader = Substitute.For<IRecompSetupDownloader>();
    private readonly List<string> _commands = [];
    private string _reportedIdentity = RecompBackend.VrProductId;
    private string _retroStatus = "current";
    private bool _questBuildSupported = true;
    private string? _questPackagePath;
    private byte[]? _downloadedBytes;

    /// <summary>A stand-in for the Quest app: an APK is a zip, and the build reads the game kit inside it.</summary>
    private static byte[] QuestAppBytes(bool withKit = true)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var manifest = new StreamWriter(archive.CreateEntry("AndroidManifest.xml").Open()))
                manifest.Write("synthetic");
            if (withKit)
            {
                using var kit = new StreamWriter(archive.CreateEntry("assets/game_kit/kit.json").Open());
                kit.Write("""{"fingerprint":"kit","products":{"base":{}}}""");
            }
        }
        return stream.ToArray();
    }

    public RecompBackendServiceTests() => SettingsTestUtils.InitializeSettingsRuntime(Path.GetFullPath("VrServiceTests/Dolphin"));

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

    private RecompInstallService Create(RecompBackend backend)
    {
        var root = PathManager.WheelWizardAppdataPath;
        var install = backend.Install(root);
        var environment = Substitute.For<IRecompEnvironment>();
        environment.Backend.Returns(backend);
        environment.InstallFolderPath.Returns(install);
        environment.CacheFolderPath.Returns(Path.Combine(backend.Root(root), "Cache"));
        environment.UserDataFolderPath.Returns(backend.UserData(root));
        environment.InstalledSetupFilePath.Returns(Path.Combine(install, "WiiCompiled-Setup.exe"));
        environment.InstallStateFilePath.Returns(Path.Combine(install, "install-state.json"));
        environment.GameFilePath.Returns(Path.Combine(root, "owned.iso"));
        environment.RetroRewindFolderPath.Returns(Path.Combine(root, "Content", "RetroRewind6"));
        Write(environment.InstalledSetupFilePath, "synthetic host");
        Write(environment.GameFilePath, "synthetic disc placeholder");
        Write(
            environment.InstallStateFilePath,
            JsonSerializer.Serialize(
                new RecompInstallState
                {
                    SchemaVersion = 1,
                    SetupVersion = "0.2.32",
                    InstallDir = install,
                    ProductId = backend.ProductId,
                    RetroRewindInstalled = true,
                    RetroWfcPayloadMode = "downloaded",
                }
            )
        );
        _github
            .GetReleasesAsync(backend.RepositoryOwner, backend.RepositoryName, Arg.Any<int>())
            .Returns(
                Task.FromResult(
                    Ok(
                        new List<GithubRelease>
                        {
                            new()
                            {
                                TagName = "v0.2.32",
                                Assets =
                                [
                                    new() { Name = "WiiCompiled-Setup.exe", BrowserDownloadUrl = "https://example.invalid/setup" },
                                    new() { Name = "WiiCompiledVR-Quest-0.4.0.apk", BrowserDownloadUrl = "https://example.invalid/app" },
                                ],
                            },
                            new()
                            {
                                TagName = "v0.2.33",
                                Assets =
                                [
                                    new() { Name = "WiiCompiled-Setup.exe", BrowserDownloadUrl = "https://example.invalid/newer-setup" },
                                    new()
                                    {
                                        Name = "WiiCompiledVR-Quest-0.5.0.apk",
                                        BrowserDownloadUrl = "https://example.invalid/newer-app",
                                    },
                                ],
                            },
                        }
                    )
                )
            );
        _downloader
            .DownloadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IProgress<int>?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var destination = call.ArgAt<string>(1);
                _fs.Directory.CreateDirectory(_fs.Path.GetDirectoryName(destination)!);
                _fs.File.WriteAllBytes(destination, _downloadedBytes ?? QuestAppBytes());
                call.ArgAt<IProgress<int>?>(2)?.Report(100);
                return Task.FromResult(Ok());
            });
        _runner
            .RunAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<Action<string>?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var command = call.ArgAt<string>(1);
                _commands.Add(command);
                var output = call.ArgAt<Action<string>?>(3);
                if (command == "--version")
                    output?.Invoke("0.2.32");
                else if (command == "--info-json")
                    output?.Invoke(
                        JsonSerializer.Serialize(
                            new
                            {
                                productId = _reportedIdentity,
                                version = "0.2.32",
                                openxrD3D12 = true,
                                questBuild = _questBuildSupported,
                            }
                        )
                    );
                else if (command.StartsWith("--build-quest"))
                {
                    var outputPath = Path.Combine(PathManager.WheelWizardAppdataPath, "MarioKartWii.wcgame");
                    Write(outputPath, "synthetic package");
                    output?.Invoke(
                        """{"type":"progress","stage":"quest-build","message":"Compiling the game for Quest...","percent":60}"""
                    );
                    output?.Invoke(
                        JsonSerializer.Serialize(
                            new
                            {
                                type = "quest-package",
                                path = _questPackagePath ?? outputPath,
                                kitFingerprint = "kit",
                                includesGameFiles = command.Contains("--include-game-files"),
                                sizeBytes = 17,
                            }
                        )
                    );
                    output?.Invoke(
                        JsonSerializer.Serialize(
                            new
                            {
                                type = "result",
                                success = true,
                                version = "0.2.32",
                                installDir = install,
                            }
                        )
                    );
                }
                else if (command.StartsWith("--check-products"))
                    output?.Invoke(
                        JsonSerializer.Serialize(
                            new
                            {
                                type = "products",
                                setupVersion = "0.2.32",
                                installDir = install,
                                rebuildRequired = _retroStatus != "current",
                                @base = new { status = "current", detail = "" },
                                retroRewind = new { status = _retroStatus, detail = "" },
                            }
                        )
                    );
                return Task.FromResult(Ok(0));
            });
        return new(
            environment,
            _runner,
            _downloader,
            Substitute.For<IRecompRetroWfcPayloadProbe>(),
            _github,
            _fs,
            Substitute.For<ILogger<RecompInstallService>>()
        );
    }

    [Theory]
    [InlineData(false, RecompGame.Base, "--launch-base")]
    [InlineData(false, RecompGame.RetroRewind, "--launch-retro")]
    [InlineData(true, RecompGame.Base, "--launch-base")]
    [InlineData(true, RecompGame.RetroRewind, "--launch-retro")]
    public async Task ReconcileAndLaunchSelectCorrectBackendAndGame(bool vr, RecompGame game, string command)
    {
        using var service = Create(vr ? RecompBackend.OpenXR : RecompBackend.Normal);
        service.SelectGame(game);
        Assert.True((await service.ReconcileForLaunchAsync()).IsSuccess);
        Assert.True((await service.LaunchAsync()).IsSuccess);
        Assert.Equal(command, _commands.Last());
        Assert.Equal(vr, _commands.Contains("--info-json"));
        Assert.False((await service.LaunchAsync()).IsSuccess); // authorization is consumed
    }

    [Fact]
    public async Task SameVersionNormalHostIsRejectedBeforeVrProductCommands()
    {
        using var service = Create(RecompBackend.OpenXR);
        _reportedIdentity = "wiicompiled";
        Assert.False((await service.CheckProductsAsync()).IsSuccess);
        Assert.DoesNotContain(_commands, command => command.StartsWith("--check-products"));
        Assert.False((await service.ReconcileForLaunchAsync()).IsSuccess);
        Assert.False((await service.LaunchAsync()).IsSuccess);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReleaseResolutionUsesSelectedRepositoryEvenWhenVersionsAreEqual(bool vr)
    {
        var backend = vr ? RecompBackend.OpenXR : RecompBackend.Normal;
        using var service = Create(backend);
        await service.GetCurrentStatusAsync();
        await _github.Received(1).GetReleasesAsync(backend.RepositoryOwner, backend.RepositoryName, Arg.Any<int>());
        await _github.DidNotReceive().GetReleasesAsync();
    }

    [Fact]
    public async Task BaseLaunchDoesNotRepairInactiveRetroRewind()
    {
        using var service = Create(RecompBackend.OpenXR);
        _retroStatus = "code-pul-changed";
        service.SelectGame(RecompGame.Base);
        Assert.True((await service.ReconcileForLaunchAsync()).IsSuccess);
        Assert.True((await service.LaunchAsync()).IsSuccess);
        Assert.DoesNotContain(_commands, command => command.StartsWith("--repair-products"));
        service.SelectGame(RecompGame.RetroRewind);
        Assert.False((await service.ReconcileForLaunchAsync()).IsSuccess); // mock does not complete repair
        Assert.Contains(_commands, command => command.StartsWith("--repair-products"));
    }

    [Fact]
    public async Task QuestBuildReportsThePackageTheVrHostWrote()
    {
        using var service = Create(RecompBackend.OpenXR);
        var apk = Path.Combine(PathManager.WheelWizardAppdataPath, "quest.apk");
        Write(apk, "synthetic apk");
        var output = Path.Combine(PathManager.WheelWizardAppdataPath, "MarioKartWii.wcgame");
        var reported = new List<int>();
        service.SelectGame(RecompGame.Base);

        var result = await service.BuildForQuestAsync(
            apk,
            output,
            includeGameFiles: true,
            progress: new SynchronousProgress(update => reported.Add(update.Percent))
        );

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);
        Assert.True(result.Value.IncludesGameFiles);
        Assert.Contains(60, reported);
        Assert.Equal(100, reported.Last());
        Assert.Contains(_commands, command => command.StartsWith("--build-quest") && command.Contains("--include-game-files"));

        // The headset plays whichever game WheelWizard is set to, and nothing else.
        Assert.Contains(_commands, command => command.StartsWith("--build-quest") && command.Contains("--quest-product base"));
    }

    [Fact]
    public async Task QuestBuildSendsTheSelectedGameAndItsPack()
    {
        using var service = Create(RecompBackend.OpenXR);
        var apk = Path.Combine(PathManager.WheelWizardAppdataPath, "quest.apk");
        Write(apk, "synthetic apk");
        var output = Path.Combine(PathManager.WheelWizardAppdataPath, "MarioKartWii.wcgame");
        service.SelectGame(RecompGame.RetroRewind);

        var result = await service.BuildForQuestAsync(apk, output, includeGameFiles: false, includeModContent: true);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);
        var command = _commands.Single(candidate => candidate.StartsWith("--build-quest"));
        Assert.Contains("--quest-product retro_rewind", command);
        Assert.Contains("--include-mod-content", command);
        Assert.Contains(Path.Combine("Content", "RetroRewind6"), command);
    }

    private static string CacheFolder => Path.Combine(RecompBackend.OpenXR.Root(PathManager.WheelWizardAppdataPath), "Cache");

    [Fact]
    public async Task QuestAppComesFromTheInstalledReleaseAndIsDownloadedOnce()
    {
        using var service = Create(RecompBackend.OpenXR);

        var found = await service.FindQuestAppAsync();
        Assert.True(found.IsSuccess, found.IsFailure ? found.Error.Message : null);
        // The installation is 0.2.32, so the newer release's app is not the one: its kit would not fit.
        Assert.Equal("v0.2.32", found.Value.ReleaseTag);
        Assert.Equal("0.4.0", found.Value.AppVersion);

        var reported = new List<int>();
        var first = await service.DownloadQuestAppAsync(found.Value, new SynchronousProgress(update => reported.Add(update.Percent)));
        Assert.True(first.IsSuccess, first.IsFailure ? first.Error.Message : null);
        Assert.Equal(CacheFolder, Path.GetDirectoryName(first.Value));
        Assert.Equal("WiiCompiledVR-Quest-0.4.0.apk", Path.GetFileName(first.Value));
        Assert.True(_fs.File.Exists(first.Value));
        Assert.Contains(100, reported);

        // The cached copy serves the next build, and a stale app of another release is dropped with it.
        var stale = Path.Combine(CacheFolder, "WiiCompiledVR-Quest-0.3.0.apk");
        Write(stale, "old app");
        var second = await service.DownloadQuestAppAsync(found.Value);
        Assert.True(second.IsSuccess);
        Assert.Equal(first.Value, second.Value);
        Assert.False(_fs.File.Exists(stale));
        await _downloader
            .Received(1)
            .DownloadAsync(found.Value.DownloadUrl, first.Value, Arg.Any<IProgress<int>?>(), Arg.Any<CancellationToken>());

        // The downloaded app is what the build compiles against.
        var output = Path.Combine(PathManager.WheelWizardAppdataPath, "MarioKartWii.wcgame");
        service.SelectGame(RecompGame.Base);
        var build = await service.BuildForQuestAsync(first.Value, output, includeGameFiles: false);
        Assert.True(build.IsSuccess, build.IsFailure ? build.Error.Message : null);
        Assert.Contains(
            _commands,
            command => command.StartsWith("--build-quest") && command.Contains("--quest-apk \"" + first.Value + "\"")
        );
    }

    [Fact]
    public async Task QuestAppDownloadIsRefusedWhenItIsNotTheApp()
    {
        using var service = Create(RecompBackend.OpenXR);
        var found = await service.FindQuestAppAsync();
        Assert.True(found.IsSuccess);

        // A zip without the game kit is not a WiiCompiled Quest app, and nothing of it is kept.
        _downloadedBytes = QuestAppBytes(withKit: false);
        var result = await service.DownloadQuestAppAsync(found.Value);
        Assert.True(result.IsFailure);
        Assert.False(_fs.File.Exists(Path.Combine(CacheFolder, "WiiCompiledVR-Quest-0.4.0.apk")));

        // A file that is not the size GitHub lists is a truncated download.
        _downloadedBytes = QuestAppBytes();
        var truncated = await service.DownloadQuestAppAsync(found.Value with { SizeBytes = _downloadedBytes.Length + 1 });
        Assert.True(truncated.IsFailure);
        var whole = await service.DownloadQuestAppAsync(found.Value with { SizeBytes = _downloadedBytes.Length });
        Assert.True(whole.IsSuccess, whole.IsFailure ? whole.Error.Message : null);
    }

    [Fact]
    public async Task QuestAppNeedsAReleaseThatPublishesIt()
    {
        using var service = Create(RecompBackend.OpenXR);
        _github
            .GetReleasesAsync(RecompBackend.OpenXR.RepositoryOwner, RecompBackend.OpenXR.RepositoryName, Arg.Any<int>())
            .Returns(
                Task.FromResult(
                    Ok(
                        new List<GithubRelease>
                        {
                            new()
                            {
                                TagName = "v0.2.32",
                                Assets = [new() { Name = "WiiCompiled-Setup.exe", BrowserDownloadUrl = "https://example.invalid/setup" }],
                            },
                        }
                    )
                )
            );

        var result = await service.FindQuestAppAsync();
        Assert.True(result.IsFailure);
        Assert.Contains("0.2.32", result.Error.Message);

        using var normal = Create(RecompBackend.Normal);
        Assert.True((await normal.FindQuestAppAsync()).IsFailure);
    }

    [Fact]
    public async Task QuestBuildRejectsAPackageForAnotherPath()
    {
        using var service = Create(RecompBackend.OpenXR);
        var apk = Path.Combine(PathManager.WheelWizardAppdataPath, "quest.apk");
        Write(apk, "synthetic apk");
        _questPackagePath = Path.Combine(PathManager.WheelWizardAppdataPath, "Other.wcgame");

        var result = await service.BuildForQuestAsync(apk, Path.Combine(PathManager.WheelWizardAppdataPath, "MarioKartWii.wcgame"), false);

        Assert.True(result.IsFailure);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task QuestBuildNeedsAVrHostThatSupportsIt(bool vr, bool supported)
    {
        using var service = Create(vr ? RecompBackend.OpenXR : RecompBackend.Normal);
        _questBuildSupported = supported;
        var apk = Path.Combine(PathManager.WheelWizardAppdataPath, "quest.apk");
        Write(apk, "synthetic apk");

        var result = await service.BuildForQuestAsync(apk, Path.Combine(PathManager.WheelWizardAppdataPath, "MarioKartWii.wcgame"), false);

        Assert.True(result.IsFailure);
        Assert.DoesNotContain(_commands, command => command.StartsWith("--build-quest"));
    }

    private sealed class SynchronousProgress(Action<RecompInstallProgress> handler) : IProgress<RecompInstallProgress>
    {
        public void Report(RecompInstallProgress value) => handler(value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UninstallRetainsBothConfigurationsNandAndOtherBackend(bool vr)
    {
        var backend = vr ? RecompBackend.OpenXR : RecompBackend.Normal;
        var other = vr ? RecompBackend.Normal : RecompBackend.OpenXR;
        using var service = Create(backend);
        var root = PathManager.WheelWizardAppdataPath;
        var kept = new[]
        {
            backend.Config(root),
            other.Config(root),
            Path.Combine(other.Install(root), "Base", "WiiCompiled.exe"),
            Path.Combine(PathManager.RecompPrivateNandFolderPath, "rksys.dat"),
            Path.Combine(PathManager.RecompNandCopyFolderPath, "RFL_DB.dat"),
            Path.Combine(root, "Content", "RetroRewind6", "Ghosts", "saved.rkg"),
        };
        foreach (var path in kept)
            Write(path, "preserved bytes");
        var cache = Path.Combine(backend.Root(root), "Cache");
        Write(Path.Combine(cache, "download.exe"), "discard");
        Assert.True((await service.UninstallAsync()).IsSuccess);
        Assert.False(_fs.Directory.Exists(backend.Install(root)));
        Assert.False(_fs.Directory.Exists(cache));
        foreach (var path in kept)
            Assert.Equal("preserved bytes", _fs.File.ReadAllText(path));
    }
}
