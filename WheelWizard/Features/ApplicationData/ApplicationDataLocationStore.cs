using System.IO.Abstractions;
using Microsoft.Win32;
using WheelWizard.Shared.IO;

namespace WheelWizard.ApplicationData;

public interface IApplicationDataLocationStore
{
    string? Load();
    void Save(string? location);
}

public sealed class FileApplicationDataLocationStore(IFileSystem fileSystem, ApplicationDataDirectories directories)
    : IApplicationDataLocationStore
{
    public string? Load() =>
        fileSystem.File.Exists(directories.OverrideFilePath) ? fileSystem.File.ReadAllText(directories.OverrideFilePath) : null;

    public void Save(string? location)
    {
        if (location is null)
            fileSystem.File.Delete(directories.OverrideFilePath);
        else
            fileSystem.WriteAllTextCreatingDirectory(directories.OverrideFilePath, location);
    }
}

public sealed class WindowsApplicationDataLocationStore : IApplicationDataLocationStore
{
    // This launcher keeps its own choice. Official Wheel Wizard's is imported once and then left alone,
    // so an empty value means "the default location" rather than "not chosen yet".
    private const string KeyPath = @"Software\\WheelWizardVR";
    private const string LegacyKeyPath = @"Software\\WheelWizard";
    private const string ValueName = "AppDataLocation";

    public string? Load()
    {
        if (!OperatingSystem.IsWindows())
            return null;
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
        if (key.GetValue(ValueName) is string saved)
            return saved;
        using var legacy = Registry.CurrentUser.OpenSubKey(LegacyKeyPath, writable: false);
        var imported = legacy?.GetValue(ValueName) as string ?? string.Empty;
        key.SetValue(ValueName, imported, RegistryValueKind.String);
        return imported;
    }

    public void Save(string? location)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException();
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
        if (key is null)
            throw new IOException("Could not open the Wheel Wizard registry key.");
        // Deleting the value would import official Wheel Wizard's choice again on the next start.
        key.SetValue(ValueName, location ?? string.Empty, RegistryValueKind.String);
    }
}
