using System.IO.Abstractions;
using WheelWizard.Services;
using WheelWizard.Settings;

namespace WheelWizard.Recomp;

public interface IRecompDolphinDataService
{
    bool IsSharingEnabled { get; }

    /// <summary>Whether private mode uses a copy originally imported from Dolphin.</summary>
    bool CopyEnabled { get; }

    string? LinkedUserFolderPath { get; }

    /// <summary>The NAND directory handed to the recomp, honoring both the sharing and the copy choice.</summary>
    string? NandFolderPath { get; }

    string? SourceNandFolderPath { get; }

    string? FindCandidateUserFolder();
    OperationResult Link(string userFolderPath);
    void SetSharingEnabled(bool enabled);
    void SetCopyEnabled(bool enabled);

    OperationResult CopyNandForRecomp();

    OperationResult ApplyNandToRecompConfig();
}

public sealed class RecompDolphinDataService(ISettingsManager settings, IRecompSettingManager recompSettings, IFileSystem fileSystem)
    : IRecompDolphinDataService
{
    private bool _nandChoiceChanged;
    public bool IsSharingEnabled => settings.Get<bool>(settings.RECOMP_USE_DOLPHIN_DATA);

    public bool CopyEnabled => settings.Get<bool>(settings.RECOMP_COPY_DOLPHIN_NAND);

    public string? LinkedUserFolderPath => ValidateUserFolder(settings.Get<string>(settings.USER_FOLDER_PATH));

    public string? NandFolderPath
    {
        get
        {
            if (!_nandChoiceChanged)
            {
                var normalConfig = PathManager.RecompConfigFilePath;
                var configured = RecompConfig.ReadString(fileSystem, normalConfig, "paths", "nand_root");
                if (!string.IsNullOrWhiteSpace(configured))
                    return fileSystem.Path.GetFullPath(configured, fileSystem.Path.GetDirectoryName(normalConfig)!);
            }
            // Sharing always means Dolphin's live NAND, even when a private clone is kept for the
            // next time sharing is disabled.
            if (IsSharingEnabled)
                return SourceNandFolderPath;

            // A missing copy never falls back to the Dolphin NAND: the user chose the copy exactly
            // so that Dolphin's own data is left alone, and a private NAND is the safe default.
            if (CopyEnabled)
                return ValidateNandFolder(PathManager.RecompNandCopyFolderPath);

            return null;
        }
    }

    public string? SourceNandFolderPath
    {
        get
        {
            // Dolphin can redirect its NAND away from <UserFolder>\Wii. Wheel Wizard already reads
            // that effective value from Dolphin.ini, so hand the same directory to WiiCompiled.
            // Falling back to the user folder covers Dolphin's default and portable layouts.
            var configuredNand = ValidateNandFolder(settings.Get<string>(settings.NAND_ROOT_PATH));
            if (configuredNand is not null)
                return configuredNand;

            var userFolder = LinkedUserFolderPath ?? FindCandidateUserFolder();
            return userFolder is null ? null : fileSystem.Path.Combine(userFolder, "Wii");
        }
    }

    public string? FindCandidateUserFolder() => ValidateUserFolder(PathManager.TryFindUserFolderPath());

    public OperationResult Link(string userFolderPath)
    {
        var validated = ValidateUserFolder(userFolderPath);
        if (validated is null)
            return Fail("The selected Dolphin folder does not contain a Wii data folder.");

        if (!settings.Set(settings.USER_FOLDER_PATH, validated))
            return Fail("Wheel Wizard could not save the Dolphin user folder.");

        SetSharingEnabled(true);
        return Ok();
    }

    public void SetSharingEnabled(bool enabled)
    {
        _nandChoiceChanged = true;
        settings.Set(settings.RECOMP_USE_DOLPHIN_DATA, enabled);
    }

    public void SetCopyEnabled(bool enabled)
    {
        _nandChoiceChanged = true;
        settings.Set(settings.RECOMP_COPY_DOLPHIN_NAND, enabled);
    }

    public OperationResult CopyNandForRecomp()
    {
        var source = SourceNandFolderPath;
        if (source is null)
            return Fail("No Dolphin Wii data folder was found to copy.");

        var destination = PathManager.RecompNandCopyFolderPath;
        var staging = destination + ".copy-" + Guid.NewGuid().ToString("N");
        var backup = destination + ".backup-" + Guid.NewGuid().ToString("N");
        try
        {
            CopyDirectory(source, staging);
            if (fileSystem.Directory.Exists(destination))
                fileSystem.Directory.Move(destination, backup);
            fileSystem.Directory.Move(staging, destination);
        }
        catch (Exception)
        {
            // Discard incomplete staging and restore the prior NAND if publication failed.
            try
            {
                if (fileSystem.Directory.Exists(staging))
                    fileSystem.Directory.Delete(staging, recursive: true);
                if (fileSystem.Directory.Exists(backup) && !fileSystem.Directory.Exists(destination))
                    fileSystem.Directory.Move(backup, destination);
            }
            catch
            {
                // Keep staging and backup for manual recovery if the filesystem is unavailable.
            }

            return Fail("Wheel Wizard could not copy the Dolphin Wii data folder.");
        }

        // Retain the previous NAND as a backup: both backends may have made progress in it.

        return Ok();
    }

    public OperationResult ApplyNandToRecompConfig()
    {
        var configPaths = new[] { RecompBackend.Normal, RecompBackend.OpenXR }
            .Select(backend => backend.Config(PathManager.WheelWizardAppdataPath))
            .Where(fileSystem.File.Exists)
            .ToArray();
        var snapshots = configPaths.ToDictionary(path => path, fileSystem.File.ReadAllText);
        try
        {
            if (configPaths.Length == 0)
                return Ok();
            var selected = NandFolderPath;
            if (IsSharingEnabled && selected is null)
                return Fail("The shared Dolphin NAND is missing. Relink it before launching.");
            var nand = selected ?? PathManager.RecompPrivateNandFolderPath;
            if (selected is not null && !fileSystem.Directory.Exists(nand))
                return Fail("The configured shared NAND is missing. Restore or relink it before launching.");
            fileSystem.Directory.CreateDirectory(nand);
            foreach (var path in configPaths)
            {
                var previous = RecompConfig.ReadString(fileSystem, path, "paths", "nand_root");
                var configDirectory = fileSystem.Path.GetDirectoryName(path)!;
                var effective = string.IsNullOrWhiteSpace(previous)
                    ? fileSystem.Path.Combine(configDirectory, "NAND")
                    : fileSystem.Path.GetFullPath(previous, configDirectory);
                if (
                    string.Equals(
                        Path.TrimEndingDirectorySeparator(effective),
                        Path.TrimEndingDirectorySeparator(nand),
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                    continue;
                var stored = Path.GetRelativePath(fileSystem.Path.GetDirectoryName(path)!, nand).Replace('\\', '/');
                RecompConfig.Set(fileSystem, path, "paths", "nand_root", RecompConfig.Quote(stored));
            }
            _nandChoiceChanged = false;
            recompSettings.ReloadSettings();
            return Ok();
        }
        catch (Exception exception)
        {
            foreach (var (path, text) in snapshots)
            {
                try
                {
                    RecompConfig.WriteAtomic(fileSystem, path, text);
                }
                catch (Exception restoreError)
                {
                    return Fail($"Could not restore the shared NAND configuration at {path}: {restoreError.Message}");
                }
            }
            return Fail($"Wheel Wizard could not update the WiiCompiled configuration: {exception.Message}");
        }
    }

    private void CopyDirectory(string sourceFolder, string destinationFolder)
    {
        fileSystem.Directory.CreateDirectory(destinationFolder);
        foreach (var file in fileSystem.Directory.GetFiles(sourceFolder))
        {
            fileSystem.File.Copy(file, fileSystem.Path.Combine(destinationFolder, fileSystem.Path.GetFileName(file)), overwrite: true);
        }

        foreach (var folder in fileSystem.Directory.GetDirectories(sourceFolder))
        {
            CopyDirectory(folder, fileSystem.Path.Combine(destinationFolder, fileSystem.Path.GetFileName(folder)));
        }
    }

    private string? ValidateUserFolder(string? userFolderPath)
    {
        if (string.IsNullOrWhiteSpace(userFolderPath))
            return null;

        try
        {
            var fullPath = fileSystem.Path.GetFullPath(userFolderPath);
            var nandPath = fileSystem.Path.Combine(fullPath, "Wii");
            return fileSystem.Directory.Exists(fullPath) && fileSystem.Directory.Exists(nandPath) ? fullPath : null;
        }
        catch
        {
            return null;
        }
    }

    private string? ValidateNandFolder(string? nandFolderPath)
    {
        if (string.IsNullOrWhiteSpace(nandFolderPath))
            return null;

        try
        {
            var fullPath = fileSystem.Path.GetFullPath(nandFolderPath);
            return fileSystem.Directory.Exists(fullPath) ? fullPath : null;
        }
        catch
        {
            return null;
        }
    }
}
