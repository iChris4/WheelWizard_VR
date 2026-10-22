using System.IO.Abstractions;
using System.IO.Compression;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Semver;
using WheelWizard.CustomDistributions.Domain;
using WheelWizard.Dolphin.Paths;
using WheelWizard.Models.Enums;
using WheelWizard.Settings;
using WheelWizard.Shared.Downloads;
using WheelWizard.Shared.IO;
using WheelWizard.Shared.Services;

namespace WheelWizard.CustomDistributions;

public class RetroRewind : IDistribution
{
    private readonly IDownloadService downloads;
    private readonly IDistributionPrompts _prompts;
    private readonly IFileSystem _fileSystem;
    private readonly IApiCaller<IRetroRewindApi> _api;
    private readonly ILogger<IDistribution> _logger;
    private readonly ISettingsManager _settingsManager;
    private readonly ICustomDistributionPaths _paths;
    private readonly IDolphinPaths _dolphinPaths;

    public RetroRewind(
        IFileSystem fileSystem,
        IApiCaller<IRetroRewindApi> api,
        ILogger<IDistribution> logger,
        ISettingsManager settingsManager,
        IDownloadService downloads,
        ICustomDistributionPaths paths,
        IDolphinPaths dolphinPaths,
        IDistributionPrompts prompts
    )
    {
        _api = api;
        this.downloads = downloads;
        _prompts = prompts;
        _fileSystem = fileSystem;
        _logger = logger;
        _settingsManager = settingsManager;
        _paths = paths;
        _dolphinPaths = dolphinPaths;
    }

    public string Title => "Retro Rewind";

    // Keep in mind, whenever we download update files from the server, they are actually 1 folder higher, so it contains this folder.
    public string FolderName => "RetroRewind6";
    public string XMLFolderName => "riivolution";
    public string XMLFileName => "RetroRewind6";

    public async Task<OperationResult> InstallAsync(DistributionOperation operation)
    {
        if (operation.CancellationToken.IsCancellationRequested)
            return Fail("Distribution installation was cancelled.");
        try
        {
            return await InstallCoreAsync(operation);
        }
        catch (Exception exception)
        {
            return Fail(exception);
        }
    }

    private async Task<OperationResult> InstallCoreAsync(DistributionOperation operation)
    {
        RetroRewindContentTransaction.Recover(_fileSystem, _paths.RootFolderPath);

        if (HasOldRksys())
        {
            if (await _prompts.ConfirmOldSaveBackupAsync())
                await BackupOldrksys();
        }
        var serverResponse = await _api.CallApiAsync(api => api.Ping()); // actual response doesnt matter
        if (serverResponse.IsFailure)
            return Fail("Could not connect to the server");

        var downloadResult = await DownloadAndExtractRetroRewind(operation);
        if (downloadResult.IsFailure)
            return downloadResult;

        if (operation.CancellationToken.IsCancellationRequested)
            return Fail("Retro Rewind installation was cancelled.");

        var updateResult = await UpdateAsync(operation);
        if (updateResult.IsFailure)
            return updateResult;

        return Ok();
    }

    /// <summary>
    /// Downloads the full distribution and publishes it through a staged transaction: the live content
    /// is only replaced once the new copy is extracted and carries a valid version, and a cancelled or
    /// interrupted install leaves the current content untouched.
    /// </summary>
    private async Task<OperationResult> DownloadAndExtractRetroRewind(DistributionOperation operation)
    {
        var root = _paths.RootFolderPath;
        var downloadedZipPath = _paths.RetroRewindArchivePath;
        try
        {
            operation.Report(new(Message: t("progress.installing_rr_first_time")));
            var url = await _api.CallApiAsync(api => api.GetInstallUrl());
            if (url.IsFailure || string.IsNullOrWhiteSpace(url.Value))
                return Fail("Failed to get Retro Rewind download URL.");
            var download = await downloads.DownloadDistributionAsync(url.Value.Trim(), downloadedZipPath, operation);
            if (download.IsFailure)
                return download.Error;
            if (!_fileSystem.File.Exists(download.Value))
                return Fail("Retro Rewind download did not complete.");
            downloadedZipPath = download.Value;
            if (operation.CancellationToken.IsCancellationRequested)
                return Fail("Retro Rewind installation was cancelled.");

            operation.Report(new(Message: t("state.extracting")));
            using var transaction = await Task.Run(() => new RetroRewindContentTransaction(_fileSystem, root));
            transaction.ResetContent();
            await Task.Run(
                () =>
                    transaction.Extract(
                        downloadedZipPath,
                        () => operation.CancellationToken.IsCancellationRequested,
                        percent => operation.Report(new(Percent: percent))
                    )
            );
            var versionFile = _fileSystem.Path.Combine(transaction.StageRoot, FolderName, "version.txt");
            if (!_fileSystem.File.Exists(versionFile) || !SemVersion.TryParse(_fileSystem.File.ReadAllText(versionFile).Trim(), out _))
                return Fail("Retro Rewind archive is missing a valid version file.");
            if (operation.CancellationToken.IsCancellationRequested)
                return Fail("Retro Rewind installation was cancelled.");
            transaction.Commit();
            return Ok();
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, exception.Message);
            return Fail(exception);
        }
        finally
        {
            if (_fileSystem.File.Exists(downloadedZipPath))
                _fileSystem.File.Delete(downloadedZipPath);

            if (_fileSystem.Directory.Exists(_paths.DownloadFolderPath))
                _fileSystem.Directory.Delete(_paths.DownloadFolderPath, recursive: true);
        }
    }

    private async Task BackupOldrksys()
    {
        var rrWfc = GetOldRksys();
        if (!_fileSystem.Directory.Exists(rrWfc))
            return;
        var rksysFiles = _fileSystem.Directory.GetFiles(rrWfc, "rksys.dat", SearchOption.AllDirectories);
        if (rksysFiles.Length == 0)
            return;
        var sourceFile = rksysFiles[0];
        var regionFolder = _fileSystem.Path.GetDirectoryName(sourceFile);
        var regionFolderName = _fileSystem.Path.GetFileName(regionFolder);
        var datFileData = await _fileSystem.File.ReadAllBytesAsync(sourceFile);
        if (regionFolderName == null)
            return;
        var destinationFolder = _fileSystem.Path.Combine(_paths.SaveFolderPath, regionFolderName);
        _fileSystem.Directory.CreateDirectory(destinationFolder);
        var destinationFile = _fileSystem.Path.Combine(destinationFolder, "rksys.dat");
        if (_fileSystem.File.Exists(destinationFile))
            return;
        await _fileSystem.File.WriteAllBytesAsync(destinationFile, datFileData);
    }

    private bool HasOldRksys()
    {
        return !string.IsNullOrWhiteSpace(GetOldRksys());
    }

    private string GetOldRksys()
    {
        // todo, maybe we should check for the existence of the file instead of the folder? and also find the oldest one?
        var rrWfcPaths = new[]
        {
            _paths.SaveFolderPath,
            // Also consider the folder with upper-case `Save`
            _fileSystem.Path.Combine(_paths.RootFolderPath, "riivolution", "Save", "RetroWFC"),
            _fileSystem.Path.Combine(_dolphinPaths.LoadFolderPath, "Riivolution", "save", "RetroWFC"),
            _fileSystem.Path.Combine(_dolphinPaths.LoadFolderPath, "Riivolution", "Save", "RetroWFC"),
            _fileSystem.Path.Combine(_dolphinPaths.LoadFolderPath, "riivolution", "save", "RetroWFC"),
            _fileSystem.Path.Combine(_dolphinPaths.LoadFolderPath, "riivolution", "Save", "RetroWFC"),
        };

        foreach (var rrWfc in rrWfcPaths)
        {
            if (!_fileSystem.Directory.Exists(rrWfc))
                continue;
            var rksysFiles = _fileSystem.Directory.GetFiles(rrWfc, "rksys.dat", SearchOption.AllDirectories);
            if (rksysFiles.Length > 0)
                return rrWfc;
        }

        return string.Empty;
    }

    private async Task<OperationResult<bool>> IsRRUpToDate(SemVersion currentVersion)
    {
        var latestVersionResult = await LatestServerVersion();
        if (latestVersionResult.IsFailure)
            return Fail("Failed to check for updates");

        var latestVersion = latestVersionResult.Value;
        var isUpToDate = currentVersion.ComparePrecedenceTo(latestVersion) >= 0;
        return isUpToDate;
    }

    private async Task<OperationResult<SemVersion>> LatestServerVersion()
    {
        var response = await _api.CallApiAsync(api => api.GetVersionFile());
        if (!response.IsSuccess || String.IsNullOrWhiteSpace(response.Value))
            return Fail("Failed to check for updates");

        var result = response.Value.Split('\n', StringSplitOptions.RemoveEmptyEntries).Last().Split(' ')[0];
        return SemVersion.Parse(result);
    }

    public async Task<OperationResult> UpdateAsync(DistributionOperation operation)
    {
        if (operation.CancellationToken.IsCancellationRequested)
            return Fail("Distribution update was cancelled.");
        try
        {
            RetroRewindContentTransaction.Recover(_fileSystem, _paths.RootFolderPath);
            var currentVersion = GetCurrentVersion();
            if (currentVersion == null)
                return await InstallAsync(operation);

            var isRRUpToDate = await IsRRUpToDate(currentVersion);
            if (isRRUpToDate.IsFailure)
                return isRRUpToDate;

            if (isRRUpToDate.Value)
                return Ok();

            //if current version is below 3.2.6 we need to do a full reinstall
            if (currentVersion.ComparePrecedenceTo(new SemVersion(3, 2, 6)) < 0)
            {
                var result = await ReinstallAsync(operation);
                return result.IsSuccess ? Ok() : result;
            }
            return await ApplyUpdates(currentVersion, operation);
        }
        catch (Exception e)
        {
            return e;
        }
    }

    private async Task<OperationResult> ApplyUpdates(SemVersion currentVersion, DistributionOperation operation)
    {
        var allVersions = await GetAllVersionData();
        var updates = GetUpdatesToApply(currentVersion, allVersions);
        if (updates.Count == 0)
            return Fail("The server did not provide the required Retro Rewind updates.");
        var root = _paths.RootFolderPath;
        using var transaction = await Task.Run(() => new RetroRewindContentTransaction(_fileSystem, root));
        var deletions = await ApplyFileDeletionsBetweenVersions(currentVersion, updates.Last().Version, transaction);
        if (deletions.IsFailure)
            return deletions;
        for (var i = 0; i < updates.Count; i++)
        {
            if (operation.CancellationToken.IsCancellationRequested)
                return Fail("Retro Rewind update was cancelled.");
            var result = await DownloadAndApplyUpdate(updates[i], updates.Count, i + 1, operation, transaction);
            if (result.IsFailure)
                return result;
            transaction.SetVersion(updates[i].Version.ToString());
        }
        if (operation.CancellationToken.IsCancellationRequested)
            return Fail("Retro Rewind update was cancelled.");
        transaction.Commit();
        return Ok();
    }

    private async Task<OperationResult> DownloadAndApplyUpdate(
        UpdateData update,
        int totalUpdates,
        int currentUpdateIndex,
        DistributionOperation operation,
        RetroRewindContentTransaction transaction
    )
    {
        var tempZipPath = _fileSystem.Path.Combine(_fileSystem.Path.GetTempPath(), _fileSystem.Path.GetRandomFileName());
        try
        {
            operation.Report(new(Message: $"{t("action.update")} {currentUpdateIndex}/{totalUpdates}: {update.Description}"));
            var download = await downloads.DownloadDistributionAsync(update.Url, tempZipPath, operation);
            if (download.IsFailure)
                return download.Error;
            var finalFile = download.Value;

            operation.Report(new(Percent: 100));
            operation.Report(new(Message: t("state.extracting")));
            await Task.Run(
                () =>
                    transaction.Extract(
                        finalFile,
                        () => operation.CancellationToken.IsCancellationRequested,
                        percent => operation.Report(new(Percent: percent))
                    )
            );

            if (_fileSystem.File.Exists(finalFile))
                _fileSystem.File.Delete(finalFile);
        }
        finally
        {
            if (_fileSystem.File.Exists(tempZipPath))
                _fileSystem.File.Delete(tempZipPath);
        }

        return Ok();
    }

    private async Task<OperationResult> ApplyFileDeletionsBetweenVersions(
        SemVersion currentVersion,
        SemVersion targetVersion,
        RetroRewindContentTransaction transaction
    )
    {
        var result = await GetFileDeletionList();
        if (result.IsFailure)
            return result.Error;
        // The deletion list is server-controlled; the transaction keeps every path inside its managed folders.
        foreach (var deletion in GetDeletionsToApply(currentVersion, targetVersion, result.Value))
            transaction.RemoveContent(deletion.Path);
        return Ok();
    }

    private struct DeletionData
    {
        public SemVersion Version;
        public string Path;
    }

    private async Task<OperationResult<List<DeletionData>>> GetFileDeletionList()
    {
        var deleteList = new List<DeletionData>();

        var deleteListOperation = await _api.CallApiAsync(api => api.GetDeletionFile());
        if (deleteListOperation.IsFailure)
            return Fail("Failed to get file deletion list");

        var lines = deleteListOperation.Value.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines)
        {
            var parts = line.Split(' ', 2);
            if (parts.Length < 2)
                continue;
            var deletionVersion = parts[0].Trim();
            var path = parts[1].Trim();
            if (string.IsNullOrWhiteSpace(deletionVersion) || string.IsNullOrWhiteSpace(path))
                continue;
            if (!SemVersion.TryParse(deletionVersion, out var parsedVersion))
                return Fail("Failed to parse version");

            var deletionData = new DeletionData { Version = parsedVersion, Path = path };
            deleteList.Add(deletionData);
        }

        return deleteList;
    }

    private struct UpdateData
    {
        public SemVersion Version;
        public string Url;
        public string Description;
    }

    private async Task<List<UpdateData>> GetAllVersionData()
    {
        var versions = new List<UpdateData>();

        var allVersionsResult = await _api.CallApiAsync(api => api.GetVersionFile());
        if (allVersionsResult.IsFailure)
            return new();
        var lines = allVersionsResult.Value.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines)
        {
            var parts = line.Split(' ', 4);
            if (parts.Length < 4)
                continue;
            var version = parts[0].Trim();
            var url = parts[1].Trim();
            var path = parts[2].Trim(); // Path unused in our program since on pc we manually decide where to extract
            var description = parts[3].Trim();
            if (string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(path))
                continue;
            // Fix old URLs using HTTP to the new endpoint
            var fixedUrl = url.Replace(RetroRewindEndpoints.LegacyBaseUrl, RetroRewindEndpoints.BaseUrl);
            if (!SemVersion.TryParse(version, out var _))
                continue;
            var parsedVersion = SemVersion.Parse(version);
            var updateData = new UpdateData
            {
                Version = parsedVersion,
                Url = fixedUrl,
                Description = description,
            };
            versions.Add(updateData);
        }
        return versions;
    }

    //todo: see if we can make this generic to the point we dont have to split up deletions and updates
    private static List<UpdateData> GetUpdatesToApply(SemVersion currentVersion, List<UpdateData> allVersions)
    {
        var updatesToApply = new List<UpdateData>();
        foreach (var update in allVersions)
        {
            if (update.Version.ComparePrecedenceTo(currentVersion) > 0)
                updatesToApply.Add(update);
        }
        return updatesToApply;
    }

    private static List<DeletionData> GetDeletionsToApply(
        SemVersion currentVersion,
        SemVersion targetVersion,
        List<DeletionData> allDeletions
    )
    {
        var deletionsToApply = new List<DeletionData>();
        allDeletions = allDeletions
            .OrderByDescending(d => d.Version, Comparer<SemVersion>.Create((a, b) => a.ComparePrecedenceTo(b)))
            .ToList();
        foreach (var deletion in allDeletions)
        {
            if (deletion.Version.ComparePrecedenceTo(currentVersion) > 0 && deletion.Version.ComparePrecedenceTo(targetVersion) <= 0)
                deletionsToApply.Add(deletion);
        }

        deletionsToApply.Reverse();
        return deletionsToApply;
    }

    public Task<OperationResult> RemoveAsync(DistributionOperation operation)
    {
        return Task.FromResult(
            TryCatch(
                () =>
                {
                    using var transaction = new RetroRewindContentTransaction(_fileSystem, _paths.RootFolderPath);
                    transaction.ResetContent();
                    transaction.Commit();
                },
                errorMessage: "Could not remove Retro Rewind content."
            )
        );
    }

    // Installing already replaces the content through a staged transaction, keeping saves and ghosts.
    public Task<OperationResult> ReinstallAsync(DistributionOperation operation) => InstallAsync(operation);

    public async Task<OperationResult<WheelWizardStatus>> GetCurrentStatusAsync()
    {
        switch (_settingsManager.CheckExtensionConfiguration())
        {
            case ExtensionConfigurationInfo.MissingDolphin:
                return WheelWizardStatus.NoDolphinExtension;
            case ExtensionConfigurationInfo.MissingRecomp:
                return WheelWizardStatus.NoRecompExtension;
            case ExtensionConfigurationInfo.ExtensionFoundOrNotNeeded:
                break;
        }

        if (!_settingsManager.PathsSetupCorrectly())
            return WheelWizardStatus.ConfigNotFinished;

        if (RetroRewindContentTransaction.HasPending(_fileSystem, _paths.RootFolderPath))
            return WheelWizardStatus.OutOfDate;
        var serverEnabled = await _api.CallApiAsync(api => api.Ping());
        var rrInstalled = GetCurrentVersion() != null;

        if (serverEnabled.IsFailure)
            return rrInstalled ? WheelWizardStatus.NoServerButInstalled : WheelWizardStatus.NoServer;

        if (!rrInstalled)
            return WheelWizardStatus.NotInstalled;

        var currentVersion = GetCurrentVersion();
        if (currentVersion == null)
            return WheelWizardStatus.NotInstalled;

        var retroRewindUpToDateResult = await IsRRUpToDate(currentVersion);
        if (retroRewindUpToDateResult.IsFailure)
            return Fail("Failed to check for updates");

        var retroRewindUpToDate = retroRewindUpToDateResult.Value;
        return !retroRewindUpToDate ? WheelWizardStatus.OutOfDate : WheelWizardStatus.Ready;
    }

    public SemVersion? GetCurrentVersion()
    {
        var versionFilePath = _fileSystem.Path.Combine(_paths.RootFolderPath, FolderName, "version.txt");
        if (!_fileSystem.File.Exists(versionFilePath))
            return null;

        var versionText = _fileSystem.File.ReadAllText(versionFilePath).Trim();
        var versionPattern = @"^\d+\.\d+\.\d+$";
        if (!Regex.IsMatch(versionText, versionPattern))
            return null;

        return SemVersion.Parse(versionText);
    }
}
