using System.Diagnostics;

namespace WheelWizard.Shared.Processes;

public interface IProcessLauncher
{
    void Start(ProcessStartInfo startInfo);

    /// <summary>Starts the process and completes when it exits, for callers that own the whole session.</summary>
    Task RunToExitAsync(ProcessStartInfo startInfo);
    void KillByName(string processName);
    IReadOnlyList<int> GetProcessIds(string processName);
}

public sealed class ProcessLauncher : IProcessLauncher
{
    public void Start(ProcessStartInfo startInfo)
    {
        using var process = Process.Start(startInfo);
        // The shell may hand documents and folders to an already running process (e.g. Explorer),
        // in which case no process is returned even though the request succeeded.
        if (process == null && !startInfo.UseShellExecute)
            throw new InvalidOperationException($"Failed to start {startInfo.FileName}.");
    }

    public async Task RunToExitAsync(ProcessStartInfo startInfo)
    {
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Failed to start {startInfo.FileName}.");
        await process.WaitForExitAsync();
    }

    public IReadOnlyList<int> GetProcessIds(string processName)
    {
        var processes = Process.GetProcessesByName(processName);
        try
        {
            return processes.Select(process => process.Id).ToArray();
        }
        finally
        {
            foreach (var process in processes)
                process.Dispose();
        }
    }

    public void KillByName(string processName)
    {
        var processes = Process.GetProcessesByName(processName);
        try
        {
            foreach (var process in processes)
                if (!process.HasExited)
                    process.Kill();
        }
        finally
        {
            foreach (var process in processes)
                process.Dispose();
        }
    }
}
