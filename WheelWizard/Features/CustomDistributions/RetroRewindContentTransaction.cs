using System.IO.Abstractions;
using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;
using WheelWizard.Recomp;

namespace WheelWizard.CustomDistributions;

/// <summary>Stages content on the same volume and journals publication so an interrupted update can roll back.</summary>
public sealed class RetroRewindContentTransaction : IDisposable
{
    private static readonly string[] ContentFolders = ["RetroRewind6", "riivolution"];
    private const string JournalName = ".retro-rewind-transaction.json";
    private readonly IFileSystem _fs;
    private readonly string _root;
    private readonly Journal _journal;
    private readonly List<string> _saveRoots = [];
    private bool _published;
    public string StageRoot { get; }

    public sealed class Journal
    {
        public string Id { get; set; } = "";
        public string Phase { get; set; } = "preparing";
        public bool[] Existed { get; set; } = [];
    }

    public RetroRewindContentTransaction(IFileSystem fs, string root)
    {
        _fs = fs;
        _root = fs.Path.GetFullPath(root);
        EnsurePlainPath(fs, _root);
        Recover(fs, _root);
        fs.Directory.CreateDirectory(_root);
        _journal = new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Existed = ContentFolders.Select(name => fs.Directory.Exists(Path.Combine(_root, name))).ToArray(),
        };
        StageRoot = Path.Combine(WorkRoot(_root, _journal), "stage");
        WriteJournal();
        try
        {
            foreach (var name in ContentFolders)
            {
                var source = Path.Combine(_root, name);
                var destination = Path.Combine(StageRoot, name);
                fs.Directory.CreateDirectory(destination);
                if (fs.Directory.Exists(source))
                    CopyTree(source, destination);
            }
            ReadSaveRoots();
        }
        catch
        {
            Recover(fs, _root);
            throw;
        }
    }

    public static bool HasPending(IFileSystem fs, string root) => fs.File.Exists(Path.Combine(root, JournalName));

    private static string WorkRoot(string root, Journal journal) => Path.Combine(root, ".rr-transaction-" + journal.Id);

    private void WriteJournal() => RecompConfig.WriteAtomic(_fs, Path.Combine(_root, JournalName), JsonSerializer.Serialize(_journal));

    public static void Recover(IFileSystem fs, string root)
    {
        var journalPath = Path.Combine(root, JournalName);
        if (!fs.File.Exists(journalPath))
            return;
        EnsurePlainPath(fs, root);
        var journal =
            JsonSerializer.Deserialize<Journal>(fs.File.ReadAllText(journalPath))
            ?? throw new IOException("Invalid Retro Rewind recovery journal.");
        if (
            !Guid.TryParseExact(journal.Id, "N", out _)
            || journal.Existed.Length != ContentFolders.Length
            || journal.Phase is not ("preparing" or "committing" or "committed")
        )
            throw new IOException("Invalid Retro Rewind recovery journal. Preserve it for recovery.");
        var work = WorkRoot(root, journal);
        EnsurePlainPath(fs, work);
        if (journal.Phase == "committing")
        {
            for (var i = 0; i < ContentFolders.Length; i++)
            {
                var live = Path.Combine(root, ContentFolders[i]);
                var backup = Path.Combine(work, "backup", ContentFolders[i]);
                if (fs.Directory.Exists(backup))
                {
                    DeleteTree(fs, live);
                    fs.Directory.Move(backup, live);
                }
                else if (!journal.Existed[i] && !fs.Directory.Exists(Path.Combine(work, "stage", ContentFolders[i])))
                    DeleteTree(fs, live);
            }
        }
        DeleteTree(fs, work);
        fs.File.Delete(journalPath);
    }

    private void CopyTree(string source, string destination)
    {
        EnsurePlainPath(_fs, source);
        _fs.Directory.CreateDirectory(destination);
        foreach (var file in _fs.Directory.EnumerateFiles(source))
        {
            EnsurePlainPath(_fs, file);
            _fs.File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), false);
        }
        foreach (var directory in _fs.Directory.EnumerateDirectories(source))
            CopyTree(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }

    private void ReadSaveRoots()
    {
        var xml = Path.Combine(StageRoot, "riivolution", "RetroRewind6.xml");
        if (!_fs.File.Exists(xml))
            return;
        using var stream = _fs.File.OpenRead(xml);
        foreach (var save in XDocument.Load(stream).Descendants("savegame"))
        {
            var external = save.Attribute("external")?.Value.Replace('\\', '/').Trim('/');
            if (!string.IsNullOrWhiteSpace(external))
                _saveRoots.Add(external + "/");
        }
    }

    public bool IsProtected(string relative)
    {
        var normalized = relative.Replace('\\', '/').Trim('/');
        var parts = normalized.Split('/');
        if (
            parts.Any(part =>
                part.Equals("save", StringComparison.OrdinalIgnoreCase)
                || part.Equals("saves", StringComparison.OrdinalIgnoreCase)
                || part.Equals("ghosts", StringComparison.OrdinalIgnoreCase)
                || part.Equals("NAND", StringComparison.OrdinalIgnoreCase)
                || part.Equals("Trophies", StringComparison.OrdinalIgnoreCase)
                || part.Equals("Patches", StringComparison.OrdinalIgnoreCase)
            )
        )
            return true;
        var name = parts[^1];
        return name.EndsWith(".rkg", StringComparison.OrdinalIgnoreCase)
            || name.Equals("rksys.dat", StringComparison.OrdinalIgnoreCase)
            || name.Equals("RFL_DB.dat", StringComparison.OrdinalIgnoreCase)
            || name.Equals("RRGameSettings.pul", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Trophy.pul", StringComparison.OrdinalIgnoreCase)
            || _saveRoots.Any(prefix => (normalized + "/").StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private string Resolve(string relative)
    {
        var normalized = relative.Replace('\\', '/').TrimStart('/');
        if (
            !ContentFolders.Any(name =>
                normalized.Equals(name, StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith(name + "/", StringComparison.OrdinalIgnoreCase)
            )
        )
            throw new IOException("Retro Rewind content path is outside its managed folders.");
        var path = _fs.Path.GetFullPath(Path.Combine(StageRoot, normalized));
        if (!path.StartsWith(StageRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Unsafe content path.");
        EnsurePlainPath(_fs, path);
        return path;
    }

    public void RemoveContent(string relative)
    {
        // The upstream deletion feed also lists obsolete Wii updater ZIPs at the root.
        // Those archives are not installed content and do not need to be removed here.
        var name = relative.Replace('\\', '/').TrimStart('/');
        if (!name.Contains('/') && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            return;
        var path = Resolve(relative);
        if (IsProtected(_fs.Path.GetRelativePath(StageRoot, path)))
            return;
        if (_fs.File.Exists(path))
            _fs.File.Delete(path);
        else if (_fs.Directory.Exists(path))
        {
            foreach (var file in _fs.Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).ToArray())
                if (!IsProtected(_fs.Path.GetRelativePath(StageRoot, file)))
                {
                    EnsurePlainPath(_fs, file);
                    _fs.File.Delete(file);
                }
        }
    }

    public void ResetContent()
    {
        RemoveContent("RetroRewind6");
        RemoveContent("riivolution/RetroRewind6.xml");
    }

    public void Extract(string zipPath, Func<bool> cancelled, Action<int>? progress = null)
    {
        using var source = _fs.File.OpenRead(zipPath);
        using var archive = new ZipArchive(source, ZipArchiveMode.Read);
        for (var i = 0; i < archive.Entries.Count; i++)
        {
            if (cancelled())
                throw new OperationCanceledException("Retro Rewind extraction cancelled.");
            var entry = archive.Entries[i];
            if (entry.FullName.EndsWith("desktop.ini", StringComparison.OrdinalIgnoreCase))
                continue;
            // The shared distribution ZIP also supplies Wii-only channels and homebrew apps.
            var first = entry.FullName.Replace('\\', '/').Split('/')[0];
            if (
                first.Equals("apps", StringComparison.OrdinalIgnoreCase)
                || first.Equals("RetroRewindChannel", StringComparison.OrdinalIgnoreCase)
                || first.Equals("RetroRewind.wad", StringComparison.OrdinalIgnoreCase)
                || first.Equals("RetroRewindVWii.wad", StringComparison.OrdinalIgnoreCase)
            )
                continue;
            var path = Resolve(entry.FullName);
            if (IsProtected(_fs.Path.GetRelativePath(StageRoot, path)) && (_fs.File.Exists(path) || _fs.Directory.Exists(path)))
                continue;
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                _fs.Directory.CreateDirectory(path);
                continue;
            }
            _fs.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var input = entry.Open();
            using var output = _fs.File.Create(path);
            input.CopyTo(output);
            progress?.Invoke((i + 1) * 100 / archive.Entries.Count);
        }
    }

    public void SetVersion(string version) => _fs.File.WriteAllText(Path.Combine(StageRoot, "RetroRewind6", "version.txt"), version);

    public void Commit()
    {
        _journal.Phase = "committing";
        WriteJournal();
        var backupRoot = Path.Combine(WorkRoot(_root, _journal), "backup");
        _fs.Directory.CreateDirectory(backupRoot);
        try
        {
            foreach (var name in ContentFolders)
            {
                var live = Path.Combine(_root, name);
                EnsurePlainPath(_fs, live);
                if (_fs.Directory.Exists(live))
                    _fs.Directory.Move(live, Path.Combine(backupRoot, name));
                _fs.Directory.Move(Path.Combine(StageRoot, name), live);
            }
            _journal.Phase = "committed";
            WriteJournal();
            _published = true;
        }
        catch
        {
            Recover(_fs, _root);
            throw;
        }
        // Successful publication wins over cleanup failure; the next operation retries cleanup.
        try
        {
            Recover(_fs, _root);
        }
        catch (IOException) { }
    }

    public void Dispose()
    {
        if (!_published)
            Recover(_fs, _root);
    }

    private static void EnsurePlainPath(IFileSystem fs, string path)
    {
        var current = fs.Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            if (
                (fs.File.Exists(current) || fs.Directory.Exists(current))
                && (fs.File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0
            )
                throw new IOException("Retro Rewind content operations cannot follow a junction or symbolic link.");
            current = fs.Path.GetDirectoryName(current);
        }
    }

    private static void DeleteTree(IFileSystem fs, string path)
    {
        if (!fs.Directory.Exists(path))
            return;
        EnsurePlainPath(fs, path);
        foreach (var entry in fs.Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories))
            EnsurePlainPath(fs, entry);
        fs.Directory.Delete(path, true);
    }
}
