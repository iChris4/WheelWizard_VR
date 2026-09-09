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
    private readonly List<string> _commands = [];
    private string _reportedIdentity = RecompBackend.VrProductId;
    private string _retroStatus = "current";

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
                                Assets = [new() { Name = "WiiCompiled-Setup.exe", BrowserDownloadUrl = "https://example.invalid/setup" }],
                            },
                        }
                    )
                )
            );
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
                            }
                        )
                    );
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
            Substitute.For<IRecompSetupDownloader>(),
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
