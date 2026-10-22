using System.Diagnostics;
using System.IO.Abstractions;

namespace WheelWizard.Recomp;

/// <summary>One fail-fast lease covers shared content and NAND through the entire game session.</summary>
public static class RecompOperationCoordinator
{
    // What a caller that passes no file system (a view, RunAsync) gets.
    private static readonly IFileSystem RealFileSystem = new Testably.Abstractions.RealFileSystem();
    private static int _active;
    public const string BusyMessage = "A game or WiiCompiled operation is already running. Close it before changing modes or shared data.";
    public static bool IsBusy => Volatile.Read(ref _active) != 0;
    public static event Action? Changed;

    public static IDisposable Acquire(string appData, IFileSystem? fileSystem = null) => AcquireCore(appData, fileSystem, false);

    /// <summary>Runs <paramref name="operation"/> under the lease, reporting a held lease as an ordinary failure.</summary>
    public static async Task<OperationResult> RunAsync(string appData, Func<Task<OperationResult>> operation)
    {
        try
        {
            using var lease = Acquire(appData);
            return await operation();
        }
        catch (IOException ex)
        {
            return Fail(ex.Message);
        }
    }

    public static IDisposable AcquireForRelocation(string appData, IFileSystem? fileSystem = null) =>
        AcquireCore(appData, fileSystem, true);

    private static IDisposable AcquireCore(string appData, IFileSystem? fileSystem, bool relocating)
    {
        if (Interlocked.CompareExchange(ref _active, 1, 0) != 0)
            throw new IOException(BusyMessage);
        var fs = fileSystem ?? RealFileSystem;
        Stream? stream = null;
        Stream? userLease = null;
        try
        {
            // Stable across app-data moves and separate portable launcher copies sharing a NAND.
            var coordinatorRoot = fs.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WheelWizardVR"
            );
            fs.Directory.CreateDirectory(coordinatorRoot);
            var userLockPath = fs.Path.Combine(coordinatorRoot, ".shared-operation.lock");
            userLease = fs.FileStream.New(userLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            fs.Directory.CreateDirectory(appData);
            var lockPath = fs.Path.Combine(appData, ".wiicompiled-shared-operation.lock");
            stream = fs.FileStream.New(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (relocating)
            {
                stream.Dispose();
                stream = null;
            }
            foreach (var backend in new[] { RecompBackend.Normal, RecompBackend.OpenXR })
            {
                var root = backend.Root(appData);
                if (!fs.Directory.Exists(root))
                    continue;
                foreach (var path in fs.Directory.EnumerateFiles(root, ".mkwc-operation-*.lock"))
                {
                    using Stream probe = fs.FileStream.New(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                }
                EnsureNoRunningProduct(fs, backend.Install(appData));
            }
            Changed?.Invoke();
            return new Lease(userLease, stream);
        }
        catch (Exception ex)
        {
            stream?.Dispose();
            userLease?.Dispose();
            Interlocked.Exchange(ref _active, 0);
            throw new IOException(BusyMessage, ex);
        }
    }

    private static void EnsureNoRunningProduct(IFileSystem fs, string install)
    {
        var prefix = fs.Path.GetFullPath(install).TrimEnd(fs.Path.DirectorySeparatorChar) + fs.Path.DirectorySeparatorChar;
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                string? executable;
                try
                {
                    executable = process.MainModule?.FileName;
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    continue;
                }
                if (executable?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true)
                    throw new IOException(BusyMessage);
            }
        }
    }

    private sealed class Lease(Stream userLease, Stream? stream) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            stream?.Dispose();
            userLease.Dispose();
            Interlocked.Exchange(ref _active, 0);
            Changed?.Invoke();
        }
    }
}
