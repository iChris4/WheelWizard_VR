using System.IO.Compression;
using System.Text.Json;
using Testably.Abstractions.Testing;
using WheelWizard.CustomDistributions;

namespace WheelWizard.Test.Features.Recomp;

public sealed class RetroRewindContentTransactionTests
{
    [Fact]
    public void WindowsPublicationFailureRestoresAlreadyMovedContent()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var root = Path.Combine(Path.GetTempPath(), "RR-transaction-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "RetroRewind6"));
        Directory.CreateDirectory(Path.Combine(root, "riivolution"));
        File.WriteAllText(Path.Combine(root, "RetroRewind6/version.txt"), "1.0.0");
        var xml = Path.Combine(root, "riivolution/RetroRewind6.xml");
        File.WriteAllText(xml, "<wiidisc/>");
        try
        {
            using var transaction = new RetroRewindContentTransaction(new Testably.Abstractions.RealFileSystem(), root);
            transaction.SetVersion("2.0.0");
            using (var blocked = new FileStream(xml, FileMode.Open, FileAccess.Read, FileShare.Read))
                Assert.Throws<IOException>(transaction.Commit);
            Assert.Equal("1.0.0", File.ReadAllText(Path.Combine(root, "RetroRewind6/version.txt")));
            Assert.Equal("<wiidisc/>", File.ReadAllText(xml));
            Assert.False(RetroRewindContentTransaction.HasPending(new Testably.Abstractions.RealFileSystem(), root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private readonly MockFileSystem _fs = new();
    private readonly string _root = Path.GetFullPath("RetroContentTests");

    private void Write(string relative, string text)
    {
        var path = Path.Combine(_root, relative);
        _fs.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _fs.File.WriteAllText(path, text);
    }

    private string Read(string relative) => _fs.File.ReadAllText(Path.Combine(_root, relative));

    private string Archive(params (string Path, string Text)[] files)
    {
        var path = Path.Combine(_root, "download.zip");
        using var output = _fs.File.Create(path);
        using var zip = new ZipArchive(output, ZipArchiveMode.Create);
        foreach (var file in files)
        {
            using var writer = new StreamWriter(zip.CreateEntry(file.Path).Open());
            writer.Write(file.Text);
        }
        return path;
    }

    private void Seed()
    {
        Write("RetroRewind6/version.txt", "1.0.0");
        Write("RetroRewind6/Tracks/old.szs", "old course");
        Write("RetroRewind6/Ghosts/player.rkg", "ghost progress");
        Write("riivolution/save/RetroWFC/RMCP/rksys.dat", "race progress");
        Write("RetroRewind6/Progress/custom.dat", "XML save progress");
        Write("riivolution/RetroRewind6.xml", "<wiidisc><patch><savegame external='/RetroRewind6/Progress'/></patch></wiidisc>");
    }

    [Fact]
    public void SharedDistributionArchiveSkipsWiiChannelsAndAddsNewGhosts()
    {
        Seed();
        var archive = Archive(
            ("apps/riivolution/boot.dol", "Wii tool"),
            ("RetroRewind.wad", "Wii channel"),
            ("RetroRewind6/Ghosts/ExpertsCT/new.rkg", "new expert ghost"),
            ("RetroRewind6/Ghosts/player.rkg", "old ghost replacement")
        );
        using var transaction = new RetroRewindContentTransaction(_fs, _root);
        transaction.Extract(archive, () => false);
        transaction.Commit();
        Assert.Equal("new expert ghost", Read("RetroRewind6/Ghosts/ExpertsCT/new.rkg"));
        Assert.Equal("ghost progress", Read("RetroRewind6/Ghosts/player.rkg"));
        Assert.False(_fs.Directory.Exists(Path.Combine(_root, "apps")));
        Assert.False(_fs.File.Exists(Path.Combine(_root, "RetroRewind.wad")));
    }

    [Fact]
    public void ReinstallAndDeletionListPreserveGhostsAndXmlDirectedSaves()
    {
        Seed();
        var archive = Archive(
            ("RetroRewind6/Tracks/new.szs", "new course"),
            ("RetroRewind6/Ghosts/player.rkg", "do not overwrite"),
            ("riivolution/save/RetroWFC/RMCP/rksys.dat", "do not overwrite"),
            ("RetroRewind6/Progress/custom.dat", "do not overwrite")
        );
        using var transaction = new RetroRewindContentTransaction(_fs, _root);
        transaction.ResetContent();
        transaction.RemoveContent("riivolution/save");
        transaction.RemoveContent("/6.12.7.zip");
        transaction.Extract(archive, () => false);
        transaction.SetVersion("2.0.0");
        Assert.Equal("1.0.0", Read("RetroRewind6/version.txt"));
        transaction.Commit();
        Assert.Equal("2.0.0", Read("RetroRewind6/version.txt"));
        Assert.Equal("new course", Read("RetroRewind6/Tracks/new.szs"));
        Assert.False(_fs.File.Exists(Path.Combine(_root, "RetroRewind6/Tracks/old.szs")));
        Assert.Equal("ghost progress", Read("RetroRewind6/Ghosts/player.rkg"));
        Assert.Equal("race progress", Read("riivolution/save/RetroWFC/RMCP/rksys.dat"));
        Assert.Equal("XML save progress", Read("RetroRewind6/Progress/custom.dat"));
        Assert.False(RetroRewindContentTransaction.HasPending(_fs, _root));
    }

    [Fact]
    public void CancelledExtractionLeavesLiveContentAndVersionUntouched()
    {
        Seed();
        var archive = Archive(("RetroRewind6/Tracks/new.szs", "new course"));
        using (var transaction = new RetroRewindContentTransaction(_fs, _root))
        {
            transaction.RemoveContent("RetroRewind6/Tracks");
            transaction.SetVersion("2.0.0");
            Assert.Throws<OperationCanceledException>(() => transaction.Extract(archive, () => true));
        }
        Assert.Equal("1.0.0", Read("RetroRewind6/version.txt"));
        Assert.Equal("old course", Read("RetroRewind6/Tracks/old.szs"));
        Assert.False(RetroRewindContentTransaction.HasPending(_fs, _root));
    }

    [Fact]
    public void RecoveryRollsBackAnInterruptedPublicationBetweenDirectoryMoves()
    {
        Seed();
        using var transaction = new RetroRewindContentTransaction(_fs, _root);
        transaction.SetVersion("2.0.0");
        var work = Path.GetDirectoryName(transaction.StageRoot)!;
        var journalPath = Path.Combine(_root, ".retro-rewind-transaction.json");
        var journal = JsonSerializer.Deserialize<RetroRewindContentTransaction.Journal>(_fs.File.ReadAllText(journalPath))!;
        journal.Phase = "committing";
        _fs.File.WriteAllText(journalPath, JsonSerializer.Serialize(journal));
        var backup = Path.Combine(work, "backup");
        _fs.Directory.CreateDirectory(backup);
        _fs.Directory.Move(Path.Combine(_root, "RetroRewind6"), Path.Combine(backup, "RetroRewind6"));
        _fs.Directory.Move(Path.Combine(transaction.StageRoot, "RetroRewind6"), Path.Combine(_root, "RetroRewind6"));
        Assert.Equal("2.0.0", Read("RetroRewind6/version.txt"));
        RetroRewindContentTransaction.Recover(_fs, _root);
        Assert.Equal("1.0.0", Read("RetroRewind6/version.txt"));
        Assert.Equal("ghost progress", Read("RetroRewind6/Ghosts/player.rkg"));
        Assert.Equal("race progress", Read("riivolution/save/RetroWFC/RMCP/rksys.dat"));
        Assert.False(RetroRewindContentTransaction.HasPending(_fs, _root));
    }

    [Theory]
    [InlineData("RetroRewind6/../../outside.dat")]
    [InlineData("OtherMod/data.bin")]
    public void ArchiveCannotChangeFilesOutsideManagedContent(string entry)
    {
        Seed();
        var archive = Archive((entry, "bad"));
        using var transaction = new RetroRewindContentTransaction(_fs, _root);
        Assert.Throws<IOException>(() => transaction.Extract(archive, () => false));
        Assert.Equal("old course", Read("RetroRewind6/Tracks/old.szs"));
    }
}
