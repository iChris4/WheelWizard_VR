using System.Diagnostics;
using System.IO.Abstractions;
using WheelWizard.Services;

namespace WheelWizard.Recomp;

/// <summary>One fail-fast lease covers shared content and NAND through the entire game session.</summary>
public static class RecompOperationCoordinator
{
    private static int _active;
    public const string BusyMessage = "A game or WiiCompiled operation is already running. Close it before changing modes or shared data.";
    public static bool IsBusy => Volatile.Read(ref _active) != 0;
    public static event Action? Changed;

    public static IDisposable Acquire() => Acquire(PathManager.WheelWizardAppdataPath);

    public static IDisposable Acquire(string appData, IFileSystem? fileSystem = null) => AcquireCore(appData, fileSystem, false);

    public static IDisposable AcquireForRelocation(string appData) => AcquireCore(appData, null, true);

    private static IDisposable AcquireCore(string appData, IFileSystem? fileSystem, bool relocating)
    {
        if (Interlocked.CompareExchange(ref _active, 1, 0) != 0)
            throw new IOException(BusyMessage);
        Stream? stream = null;
        Stream? userLease = null;
        try
        {
            // Stable across app-data moves and separate portable launcher copies sharing a NAND.
            var coordinatorRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WheelWizardVR");
            if (fileSystem is null)
                Directory.CreateDirectory(coordinatorRoot);
            else
                fileSystem.Directory.CreateDirectory(coordinatorRoot);
            var userLockPath = Path.Combine(coordinatorRoot, ".shared-operation.lock");
            userLease = fileSystem is null
                ? new FileStream(userLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)
                : fileSystem.FileStream.New(userLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (fileSystem is null)
                Directory.CreateDirectory(appData);
            else
                fileSystem.Directory.CreateDirectory(appData);
            var lockPath = Path.Combine(appData, ".wiicompiled-shared-operation.lock");
            stream = fileSystem is null
                ? new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)
                : fileSystem.FileStream.New(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (relocating)
            {
                stream.Dispose();
                stream = null;
            }
            foreach (var backend in new[] { RecompBackend.Normal, RecompBackend.OpenXR })
            {
                var root = backend.Root(appData);
                if (!(fileSystem?.Directory.Exists(root) ?? Directory.Exists(root)))
                    continue;
                var locks =
                    fileSystem?.Directory.EnumerateFiles(root, ".mkwc-operation-*.lock")
                    ?? Directory.EnumerateFiles(root, ".mkwc-operation-*.lock");
                foreach (var path in locks)
                {
                    using Stream probe = fileSystem is null
                        ? new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)
                        : fileSystem.FileStream.New(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                }
                EnsureNoRunningProduct(backend.Install(appData));
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

    private static void EnsureNoRunningProduct(string install)
    {
        var prefix = Path.GetFullPath(install).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
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
