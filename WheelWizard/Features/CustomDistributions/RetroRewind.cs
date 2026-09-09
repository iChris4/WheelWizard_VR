using System.IO.Abstractions;
using System.IO.Compression;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Semver;
using WheelWizard.CustomDistributions.Domain;
using WheelWizard.Helpers;
using WheelWizard.Models.Enums;
using WheelWizard.Services;
using WheelWizard.Settings;
using WheelWizard.Shared.Services;
using WheelWizard.Views.Popups.Generic;

namespace WheelWizard.CustomDistributions;

public class RetroRewind : IDistribution
{
    private readonly IFileSystem _fileSystem;
    private readonly IApiCaller<IRetroRewindApi> _api;
    private readonly ILogger<IDistribution> _logger;
    private readonly ISettingsManager _settingsManager;

    public RetroRewind(
        IFileSystem fileSystem,
        IApiCaller<IRetroRewindApi> api,
        ILogger<IDistribution> logger,
        ISettingsManager settingsManager
    )
    {
        _api = api;
        _fileSystem = fileSystem;
        _logger = logger;
        _settingsManager = settingsManager;
    }

    public string Title => "Retro Rewind";

    // Keep in mind, whenever we download update files from the server, they are actually 1 folder higher, so it contains this folder.
    public string FolderName => "RetroRewind6";
    public string XMLFolderName => "riivolution";
    public string XMLFileName => "RetroRewind6";

    public async Task<OperationResult> InstallAsync(ProgressWindow progressWindow)
    {
        try
        {
            return await InstallCoreAsync(progressWindow);
        }
        catch (Exception exception)
        {
            return Fail(exception);
        }
    }

    private async Task<OperationResult> InstallCoreAsync(ProgressWindow progressWindow)
    {
        RetroRewindContentTransaction.Recover(_fileSystem, PathManager.RiivolutionWhWzFolderPath);

        if (HasOldRksys())
        {
            var rksysQuestion = new YesNoWindow()
                .SetMainText(t("question.old_rksys_found.title"))
                .SetExtraText(t("question.old_rksys_found.extra"));
            if (await rksysQuestion.AwaitAnswer())
                await BackupOldrksys();
        }
        var serverResponse = await _api.CallApiAsync(api => api.Ping()); // actual response doesnt matter
        if (serverResponse.IsFailure)
            return Fail("Could not connect to the server");

        var downloadResult = await DownloadAndExtractRetroRewind(progressWindow);
        if (downloadResult.IsFailure)
            return downloadResult;

        if (progressWindow.WasCancellationRequested)
            return Ok();

        var updateResult = await UpdateAsync(progressWindow);
        if (updateResult.IsFailure)
            return updateResult;

        return Ok();
    }

    private async Task<OperationResult> DownloadAndExtractRetroRewind(ProgressWindow progressWindow)
    {
        var root = PathManager.RiivolutionWhWzFolderPath;
        var downloadedZipPath = PathManager.RetroRewindTempFile;
        try
        {
            progressWindow.SetExtraText(t("progress.installing_rr_first_time"));
            var url = await _api.CallApiAsync(api => api.GetInstallUrl());
            if (url.IsFailure || string.IsNullOrWhiteSpace(url.Value))
                return Fail("Failed to get Retro Rewind download URL.");
            var downloaded = await DownloadHelper.DownloadToLocationAsync(url.Value.Trim(), downloadedZipPath, progressWindow);
            if (string.IsNullOrWhiteSpace(downloaded) || !_fileSystem.File.Exists(downloaded))
                return Fail("Retro Rewind download did not complete.");
            downloadedZipPath = downloaded;
            if (progressWindow.WasCancellationRequested)
                return Fail("Retro Rewind installation cancelled.");
            using var transaction = await Task.Run(() => new RetroRewindContentTransaction(_fileSystem, root));
            transaction.ResetContent();
            await Task.Run(
                () =>
                    transaction.Extract(
                        downloadedZipPath,
                        () => progressWindow.WasCancellationRequested,
                        percent => Dispatcher.UIThread.Post(() => progressWindow.UpdateProgress(percent))
                    )
            );
            var versionFile = _fileSystem.Path.Combine(transaction.StageRoot, FolderName, "version.txt");
            if (!_fileSystem.File.Exists(versionFile) || !SemVersion.TryParse(_fileSystem.File.ReadAllText(versionFile).Trim(), out _))
                return Fail("Retro Rewind archive is missing a valid version file.");
            if (progressWindow.WasCancellationRequested)
                return Fail("Retro Rewind installation cancelled.");
            transaction.Commit();
            return Ok();
        }
        catch (Exception exception)
        {
            return Fail(exception);
        }
        finally
        {
            if (_fileSystem.File.Exists(downloadedZipPath))
                _fileSystem.File.Delete(downloadedZipPath);
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
        var destinationFolder = _fileSystem.Path.Combine(PathManager.SaveFolderPath, regionFolderName);
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
            PathManager.SaveFolderPath,
            // Also consider the folder with upper-case `Save`
            _fileSystem.Path.Combine(PathManager.RiivolutionWhWzFolderPath, "riivolution", "Save", "RetroWFC"),
            _fileSystem.Path.Combine(PathManager.LoadFolderPath, "Riivolution", "save", "RetroWFC"),
            _fileSystem.Path.Combine(PathManager.LoadFolderPath, "Riivolution", "Save", "RetroWFC"),
            _fileSystem.Path.Combine(PathManager.LoadFolderPath, "riivolution", "save", "RetroWFC"),
            _fileSystem.Path.Combine(PathManager.LoadFolderPath, "riivolution", "Save", "RetroWFC"),
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

    public async Task<OperationResult> UpdateAsync(ProgressWindow progressWindow)
    {
        try
        {
            RetroRewindContentTransaction.Recover(_fileSystem, PathManager.RiivolutionWhWzFolderPath);
            var currentVersion = GetCurrentVersion();
            if (currentVersion == null)
                return await InstallAsync(progressWindow);

            var isRRUpToDate = await IsRRUpToDate(currentVersion);
            if (isRRUpToDate.IsFailure)
                return isRRUpToDate;

            if (isRRUpToDate.Value)
                return Ok();

            //if current version is below 3.2.6 we need to do a full reinstall
            if (currentVersion.ComparePrecedenceTo(new SemVersion(3, 2, 6)) < 0)
            {
                var result = await ReinstallAsync(progressWindow);
                return result.IsSuccess ? Ok() : result;
            }
            return await ApplyUpdates(currentVersion, progressWindow);
        }
        catch (Exception e)
        {
            return e;
        }
    }

    private async Task<OperationResult> ApplyUpdates(SemVersion currentVersion, ProgressWindow progressWindow)
    {
        var allVersions = await GetAllVersionData();
        var updates = GetUpdatesToApply(currentVersion, allVersions);
        if (updates.Count == 0)
            return Fail("The server did not provide the required Retro Rewind updates.");
        var root = PathManager.RiivolutionWhWzFolderPath;
        using var transaction = await Task.Run(() => new RetroRewindContentTransaction(_fileSystem, root));
        var deletions = await ApplyFileDeletionsBetweenVersions(currentVersion, updates.Last().Version, transaction);
        if (deletions.IsFailure)
            return deletions;
        for (var i = 0; i < updates.Count; i++)
        {
            var result = await DownloadAndApplyUpdate(updates[i], updates.Count, i + 1, progressWindow, transaction);
            if (result.IsFailure)
                return result;
            if (progressWindow.WasCancellationRequested)
                return Fail("Retro Rewind update cancelled.");
            transaction.SetVersion(updates[i].Version.ToString());
        }
        if (progressWindow.WasCancellationRequested)
            return Fail("Retro Rewind update cancelled.");
        transaction.Commit();
        return Ok();
    }

    private async Task<OperationResult> DownloadAndApplyUpdate(
        UpdateData update,
        int totalUpdates,
        int currentUpdateIndex,
        ProgressWindow popupWindow,
        RetroRewindContentTransaction transaction
    )
    {
        var tempZipPath = _fileSystem.Path.Combine(_fileSystem.Path.GetTempPath(), _fileSystem.Path.GetRandomFileName());
        try
        {
            popupWindow.SetExtraText($"{t("action.update")} {currentUpdateIndex}/{totalUpdates}: {update.Description}");
            var finalFile = await DownloadHelper.DownloadToLocationAsync(update.Url, tempZipPath, popupWindow);

            if (finalFile == null)
                return Fail("Failed to download update file");

            popupWindow.UpdateProgress(100);
            popupWindow.SetExtraText(t("state.extracting"));
            await Task.Run(
                () =>
                    transaction.Extract(
                        finalFile,
                        () => popupWindow.WasCancellationRequested,
                        percent => Dispatcher.UIThread.Post(() => popupWindow.UpdateProgress(percent))
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
            var fixedUrl = url.Replace(Endpoints.OldRRUrl, Endpoints.RRUrl);
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

    public Task<OperationResult> RemoveAsync(ProgressWindow progressWindow)
    {
        return Task.FromResult(
            TryCatch(
                () =>
                {
                    using var transaction = new RetroRewindContentTransaction(_fileSystem, PathManager.RiivolutionWhWzFolderPath);
                    transaction.ResetContent();
                    transaction.Commit();
                },
                errorMessage: "Could not remove Retro Rewind content."
            )
        );
    }

    public Task<OperationResult> ReinstallAsync(ProgressWindow progressWindow) => InstallAsync(progressWindow);

    public async Task<OperationResult<WheelWizardStatus>> GetCurrentStatusAsync()
    {
        if (!_settingsManager.PathsSetupCorrectly())
            return WheelWizardStatus.ConfigNotFinished;

        if (RetroRewindContentTransaction.HasPending(_fileSystem, PathManager.RiivolutionWhWzFolderPath))
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
        var versionFilePath = _fileSystem.Path.Combine(PathManager.RiivolutionWhWzFolderPath, FolderName, "version.txt");
        if (!_fileSystem.File.Exists(versionFilePath))
            return null;

        var versionText = _fileSystem.File.ReadAllText(versionFilePath).Trim();
        var versionPattern = @"^\d+\.\d+\.\d+$";
        if (!Regex.IsMatch(versionText, versionPattern))
            return null;

        return SemVersion.Parse(versionText);
    }
}
