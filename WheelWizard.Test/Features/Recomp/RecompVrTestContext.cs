using Testably.Abstractions.Testing;
using WheelWizard.ApplicationData;
using WheelWizard.Dolphin.Paths;
using WheelWizard.Recomp;
using WheelWizard.Settings;
using WheelWizard.Shared.Platform;
using WheelWizard.Shared.Processes;
using WheelWizard.Test.Features.Settings;

namespace WheelWizard.Test.Features.Recomp;

/// <summary>
/// A launcher data folder on a mock file system, holding both backends' layouts, with the services the
/// VR tests build on.
/// </summary>
internal sealed class RecompVrTestContext
{
    public MockFileSystem FileSystem { get; } = new();
    public IApplicationDataLocation ApplicationData { get; } = Substitute.For<IApplicationDataLocation>();
    public string Root { get; }
    public IRecompPaths Paths { get; }
    public RecompBackendSelection Selection { get; }

    public RecompVrTestContext()
    {
        Root = FileSystem.Path.GetFullPath("/vr-tests/CT-MKWII");
        ApplicationData.DirectoryPath.Returns(Root);
        Paths = new RecompPaths(ApplicationData, FileSystem, new RuntimeEnvironment());
        Selection = new RecompBackendSelection(ApplicationData, Paths);
    }

    public SettingsManager CreateSettings(IRecompSettingManager? recompSettings = null, IWhWzSettingManager? whWzSettings = null) =>
        new(
            whWzSettings ?? Substitute.For<IWhWzSettingManager>(),
            Substitute.For<IDolphinSettingManager>(),
            recompSettings ?? Substitute.For<IRecompSettingManager>(),
            FileSystem,
            SettingsTestUtils.CreateSettingsSignalBus(),
            new DolphinPathResolver(FileSystem, new RuntimeEnvironment()),
            ApplicationData,
            Paths,
            new RuntimeEnvironment(),
            Substitute.For<IUnixCommandService>(),
            Selection
        );

    public void Write(string path, string text)
    {
        FileSystem.Directory.CreateDirectory(FileSystem.Path.GetDirectoryName(path)!);
        FileSystem.File.WriteAllText(path, text);
    }
}

/// <summary>
/// The shared operation lease is one per process, so tests that take it, or read whether it is held,
/// must not overlap one another.
/// </summary>
[CollectionDefinition("SharedOperationLease", DisableParallelization = true)]
public sealed class SharedOperationLeaseCollection;
