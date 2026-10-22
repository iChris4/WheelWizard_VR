using System.ComponentModel;
using System.IO.Abstractions;
using Testably.Abstractions;
using WheelWizard.ApplicationData;
using WheelWizard.Launching;
using WheelWizard.Models.Enums;
using WheelWizard.Recomp;
using WheelWizard.Settings;
using WheelWizard.Shared.Calendar;
using WheelWizard.Views.Components;

namespace WheelWizard.Views.Shell.ViewModels;

public sealed record HomeLaunchPrompt(string MainText, string ExtraText, string YesText, string NoText);

public interface IHomePresentation
{
    void ShowError(OperationError error);
    void OpenSettings();
    void SetApplicationInteractable(bool interactable);
    Task ShowLaunchPromptAsync(HomeLaunchPrompt prompt);
}

public sealed class HomeViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IReadOnlyList<ILauncher> _launchers;
    private int _launcherIndex;
    private readonly IDolphinLaunchService _dolphin;
    private readonly ISettingsManager _settings;
    private readonly IHomePresentation _presentation;
    private readonly ISeasonalCalendar _calendar;
    private readonly IRandomSystem _random;
    private readonly TimeProvider _time;
    private readonly IApplicationDataLocation _applicationData;
    private readonly IFileSystem _fileSystem;
    private readonly CancellationTokenSource _lifetime = new();
    private int _refreshGeneration;
    private bool _disposed;
    private bool _busy;
    private WheelWizardStatus _status = WheelWizardStatus.Loading;

    public HomeViewModel(
        ILauncherProvider launchers,
        IDolphinLaunchService dolphin,
        ISettingsManager settings,
        IHomePresentation presentation,
        ISeasonalCalendar calendar,
        IRandomSystem random,
        TimeProvider time,
        IApplicationDataLocation applicationData,
        IFileSystem fileSystem
    )
    {
        _launchers = launchers.GetLaunchers();
        _dolphin = dolphin;
        _settings = settings;
        _presentation = presentation;
        _calendar = calendar;
        _random = random;
        _time = time;
        _applicationData = applicationData;
        _fileSystem = fileSystem;
    }

    private ILauncher Launcher => _launchers[_launcherIndex];

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? MainActionStarted;
    public WheelWizardStatus Status => _status;
    public bool IsBusy => _busy;

    // A game or WiiCompiled operation, from this launcher or another copy of it, owns the shared data.
    public bool IsInteractable => !_busy && !_disposed && !RecompOperationCoordinator.IsBusy;
    public bool IsDolphinVisible => !_settings.IsRecompModeActive();
    public bool AnimationsEnabled => _settings.Get<bool>(_settings.ENABLE_ANIMATIONS);
    public string GameTitle => DisplayName(Launcher);

    // A recomp launcher names its backend on a line of its own under the game.
    public string? BackendTitle => (Launcher as RecompLauncher)?.BackendName;
    public bool HasBackendTitle => Launcher is RecompLauncher;

    // One segment per launcher: WiiCompiled offers Retro Rewind and the base game.
    public bool HasGameSelector => _launchers.Count > 1;
    public IReadOnlyList<string> GameNames => _launchers.Select(DisplayName).ToList();
    public int SelectedGameIndex => _launcherIndex;

    private string DisplayName(ILauncher launcher)
    {
        var name = launcher is RecompLauncher recomp ? recomp.GameName : launcher.GameTitle;
        return name == "Retro Rewind" && _calendar.IsAprilFirst ? "Retro Beefbai" : name;
    }

    /// <summary>
    /// Points Play at another game. A running game or install owns the installation, so the switch
    /// waits rather than pointing Play at a game whose state cannot be read right now.
    /// </summary>
    public bool SelectGame(int index)
    {
        if (index < 0 || index >= _launchers.Count)
            return false;
        if (index == _launcherIndex)
            return true;
        if (!IsInteractable)
            return false;
        _launcherIndex = index;
        _ = RefreshAsync();
        return true;
    }

    /// <summary>Re-reads whether a shared operation holds the installation; call it on the UI thread.</summary>
    public void RefreshOperationState() => NotifyStateChanged();

    public bool CanExecuteMain =>
        IsInteractable
        && _status
            is WheelWizardStatus.NoServerButInstalled
                or WheelWizardStatus.NoDolphin
                or WheelWizardStatus.ConfigNotFinished
                or WheelWizardStatus.NotInstalled
                or WheelWizardStatus.OutOfDate
                or WheelWizardStatus.Ready;
    public bool CanLaunchDolphin =>
        IsInteractable
        && IsDolphinVisible
        && _settings.PathsSetupCorrectly()
        && _status is not (WheelWizardStatus.Loading or WheelWizardStatus.NoDolphin or WheelWizardStatus.NoDolphinExtension);

    public string ButtonText =>
        _status switch
        {
            WheelWizardStatus.NoServer => t("state.no_server"),
            WheelWizardStatus.NoDolphinExtension => t("state.no_dolphin_extension"),
            WheelWizardStatus.NoRecompExtension => t("state.no_recomp_extension"),
            WheelWizardStatus.NoServerButInstalled => t("action.play_offline"),
            WheelWizardStatus.NoDolphin => "Dolphin not setup",
            WheelWizardStatus.ConfigNotFinished => t("state.config_not_finished"),
            WheelWizardStatus.NotInstalled => t("action.install"),
            WheelWizardStatus.OutOfDate => t("action.update"),
            WheelWizardStatus.Ready => t("action.play"),
            _ => t("state.loading"),
        };

    public ActionButtonTone ButtonTone =>
        _status switch
        {
            WheelWizardStatus.NoServer or WheelWizardStatus.NoRecompExtension or WheelWizardStatus.NoDolphinExtension =>
                ActionButtonTone.Danger,
            WheelWizardStatus.Ready => ActionButtonTone.Brand,
            WheelWizardStatus.Loading => ActionButtonTone.Secondary,
            _ => ActionButtonTone.Primary,
        };

    public bool IsLoading => IsBusy || Status == WheelWizardStatus.Loading;

    public string IconName =>
        _status switch
        {
            WheelWizardStatus.NoServer or WheelWizardStatus.NoRecompExtension or WheelWizardStatus.NoDolphinExtension => "RoadError",
            WheelWizardStatus.NoServerButInstalled or WheelWizardStatus.Ready => "Play",
            WheelWizardStatus.NoDolphin or WheelWizardStatus.ConfigNotFinished => "Settings",
            WheelWizardStatus.NotInstalled or WheelWizardStatus.OutOfDate => "Download",
            _ => string.Empty,
        };

    public async Task RefreshAsync()
    {
        if (_disposed)
            return;
        var generation = ++_refreshGeneration;
        _status = WheelWizardStatus.Loading;
        NotifyStateChanged();
        WheelWizardStatus status;
        try
        {
            status = await Launcher.GetCurrentStatus();
        }
        catch (Exception ex)
        {
            status = WheelWizardStatus.NoServer;
            if (!_disposed && generation == _refreshGeneration)
                _presentation.ShowError(new OperationError { Message = "Failed to check game status.", Exception = ex });
        }
        if (_disposed || generation != _refreshGeneration)
            return;
        _status = status;
        NotifyStateChanged();
    }

    public async Task ExecuteMainAsync()
    {
        if (!CanExecuteMain)
            return;
        var action = _status;
        var setup = action is WheelWizardStatus.NotInstalled or WheelWizardStatus.OutOfDate;
        await RunActionAsync(
            async () =>
            {
                if (_calendar.IsAprilFirst && action is WheelWizardStatus.Ready or WheelWizardStatus.NoServerButInstalled)
                    await ShowAprilFirstPromptsAsync();
                MainActionStarted?.Invoke(this, EventArgs.Empty);
                if (action is WheelWizardStatus.NoDolphin or WheelWizardStatus.ConfigNotFinished)
                {
                    _presentation.OpenSettings();
                    return Ok();
                }
                return action switch
                {
                    WheelWizardStatus.NotInstalled => await Launcher.Install(),
                    WheelWizardStatus.OutOfDate => await Launcher.Update(),
                    _ => await Launcher.Launch(),
                };
            },
            setup
        );
    }

    // Dolphin owns its preflight choices and launch-error dialogs; only unexpected exceptions need Home's error handling.
    public Task LaunchDolphinAsync() =>
        CanLaunchDolphin ? RunActionAsync(LaunchDolphinSessionAsync, setup: false, showResultErrors: false) : Task.CompletedTask;

    // The lease covers the whole Dolphin session, so shared content and the NAND cannot change under it.
    private async Task<OperationResult> LaunchDolphinSessionAsync()
    {
        IDisposable lease;
        try
        {
            lease = RecompOperationCoordinator.Acquire(_applicationData.DirectoryPath, _fileSystem);
        }
        catch (IOException ex)
        {
            _presentation.ShowError(Fail(ex.Message));
            return Ok();
        }
        using (lease)
            return await _dolphin.LaunchDolphin(waitForExit: true);
    }

    private async Task RunActionAsync(Func<Task<OperationResult>> action, bool setup, bool showResultErrors = true)
    {
        _busy = true;
        ++_refreshGeneration;
        NotifyStateChanged();
        var started = _time.GetTimestamp();
        try
        {
            if (setup)
                _presentation.SetApplicationInteractable(false);
            var result = await action();
            if (showResultErrors && result.IsFailure && !_disposed)
                _presentation.ShowError(result.Error);
        }
        catch (Exception ex)
        {
            if (!_disposed)
                _presentation.ShowError(new OperationError { Message = "The requested game operation failed.", Exception = ex });
        }
        finally
        {
            if (setup)
                _presentation.SetApplicationInteractable(true);
            var remaining = TimeSpan.FromSeconds(2) - _time.GetElapsedTime(started);
            try
            {
                if (remaining > TimeSpan.Zero && !_disposed)
                    await Task.Delay(remaining, _time, _lifetime.Token);
            }
            catch (OperationCanceledException) when (_disposed) { }
            _busy = false;
            if (!_disposed)
                await RefreshAsync();
        }
    }

    private async Task ShowAprilFirstPromptsAsync()
    {
        HomeLaunchPrompt[] prompts =
        [
            new("You wanna start the game?", "This feels suspiciously productive.", "Yeah", "Nah"),
            new("Eeeeh not feeling like it", "Try asking a little nicer next time.", "Please", "Whatever"),
            new("Launch Retro Beefbai?", "I am consulting the ancient wheel.", "Do it", "Nope"),
            new("You again?", "The game is pretending not to notice you.", "Open it", "Leave it"),
            new("Starting the game already?", "That was fast. Almost too fast.", "Fine", "Hold on"),
        ];
        _random.Random.Shared.Shuffle(prompts);
        foreach (var prompt in prompts.Take(4))
            await _presentation.ShowLaunchPromptAsync(prompt);
    }

    private void NotifyStateChanged() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        ++_refreshGeneration;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
