using System.IO.Compression;
using System.Text.Json;
using DifferentialBackup.Systems;
using DifferentialBackup.Test.Helpers;
using DifferentialBackup.Utilities;
using DOPipeline.Entities;
using DOPipeline.Storage;

namespace DifferentialBackup.Test.Utilities;

public sealed class RestoreNamePreflightTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NamePreflight-" + Guid.NewGuid().ToString("N"));
    private string Destination => Path.Combine(_root, "restore");
    public RestoreNamePreflightTests() { Directory.CreateDirectory(Destination); }

    [Theory]
    [InlineData("File", "file")]
    [InlineData("é", "e\u0301")]
    [InlineData("Å", "A\u030a")]
    public void CollisionDecisionUsesTheActualFilesystem(string first, string second)
    {
        File.WriteAllText(Path.Combine(Destination, first), "keep existing bytes");
        var equivalent = File.Exists(Path.Combine(Destination, second));
        var error = Record.Exception(() => RestoreNamePreflight.Validate(Destination,
            new[] { (Path.Combine(Destination, first), false), (Path.Combine(Destination, second), false) }));
        Assert.Equal(equivalent, error is InvalidDataException);
        Assert.Equal("keep existing bytes", File.ReadAllText(Path.Combine(Destination, first)));
        Assert.Empty(Directory.GetFileSystemEntries(Destination, ".db-*"));
    }

    [Theory]
    [InlineData("Folder", "folder")]
    [InlineData("é", "e\u0301")]
    public void DirectoryCollisionsAreDetectedBeforeReplacingAnyFiles(string first, string second)
    {
        Directory.CreateDirectory(Path.Combine(Destination, first));
        File.WriteAllText(Path.Combine(Destination, first, "keep.txt"), "unchanged");
        var equivalent = Directory.Exists(Path.Combine(Destination, second));
        var error = Record.Exception(() => RestoreNamePreflight.Validate(Destination,
            new[] { (Path.Combine(Destination, first, "a"), false), (Path.Combine(Destination, second, "b"), false) }));
        Assert.Equal(equivalent, error is InvalidDataException);
        Assert.Equal("unchanged", File.ReadAllText(Path.Combine(Destination, first, "keep.txt")));
    }

    [Fact]
    public void FileVersusDirectoryConflictDoesNotOverwrite()
    {
        var target = Path.Combine(Destination, "conflict");
        File.WriteAllText(target, "original");
        Assert.Throws<InvalidDataException>(() => RestoreNamePreflight.Validate(Destination,
            new[] { (target, false), (Path.Combine(target, "file"), false) }));
        Assert.Equal("original", File.ReadAllText(target));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("/rooted")]
    [InlineData("a/../escape")]
    [InlineData("a//file")]
    [InlineData("a/./file")]
    [InlineData("a//")]
    public void PortableArchiveNamesRejectUnsafePaths(string name) =>
        Assert.Throws<InvalidDataException>(() => ArchivePath.ToAccessPath(Destination, name, true));

    [Theory]
    [InlineData("CON")]
    [InlineData("nul.txt")]
    [InlineData("COM1.dat")]
    [InlineData("LPT¹")]
    [InlineData("trailing.")]
    [InlineData("colon:name")]
    [InlineData("literal\\backslash")]
    public void WindowsUnrepresentableNamesAreRejectedAndUnixSpellingIsPreserved(string name)
    {
        if (OperatingSystem.IsWindows())
            Assert.Throws<InvalidDataException>(() => ArchivePath.ToAccessPath(Destination, name, true));
        else Assert.Equal(Path.Combine(Destination, name), ArchivePath.ToAccessPath(Destination, name, true));
    }

    [Fact]
    public void ArchiveCollisionPreflightDoesNotReplaceTheFirstTarget()
    {
        var first = Path.Combine(Destination, "File");
        File.WriteAllText(first, "keep");
        var insensitive = File.Exists(Path.Combine(Destination, "file"));
        var backup = Path.Combine(_root, "backup");
        var date = new DateTime(2026, 1, 2);
        var set = Path.Combine(backup, date.ToString("yyyyMMddHHmmss") + ".backup");
        Directory.CreateDirectory(set);
        var archive = Path.Combine(set, "part-000001.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
            foreach (var name in new[] { "File", "file" })
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write(name);
            }
        File.WriteAllText(Path.Combine(set, "manifest.json"), JsonSerializer.Serialize(new BackupManifest
        {
            RunId = Guid.NewGuid(), BackupDate = date,
            Parts = new() { new() { PartNumber = 1, ArchiveFileName = Path.GetFileName(archive), ArchiveBytes = new FileInfo(archive).Length } }
        }));
        var result = new RestoreSystem(backup, date, Destination).Execute(new Entity(), new ComponentStorage());
        Assert.Equal(!insensitive, result.IsSuccess);
        Assert.Equal(insensitive ? "keep" : "File", File.ReadAllText(first));
    }

    [Theory]
    [InlineData(".differential-restore.json")]
    [InlineData(".differential-restore.log")]
    [InlineData(".differential-restore.lock")]
    public void RestoreMetadataCannotOverwriteAnArchivedRootName(string name)
    {
        var target = Path.Combine(Destination, name);
        File.WriteAllText(target, "preserve");
        Assert.Throws<InvalidDataException>(() => RestoreNamePreflight.Validate(Destination, new[] { (target, false) }));
        Assert.Equal("preserve", File.ReadAllText(target));
    }

    [WindowsCaseSensitiveFact]
    public void MixedDirectoryCasePoliciesAreCheckedBeforeAnyReplacement()
    {
        var sensitive = Path.Combine(Destination, "sensitive");
        var ordinary = Path.Combine(Destination, "ordinary");
        Directory.CreateDirectory(sensitive);
        Directory.CreateDirectory(ordinary);
        Assert.True(WindowsCaseSensitiveFactAttribute.Enable(sensitive));
        var first = Path.Combine(sensitive, "File");
        var second = Path.Combine(sensitive, "file");
        var regular = Path.Combine(ordinary, "File");
        File.WriteAllText(first, "first");
        File.WriteAllText(second, "second");
        File.WriteAllText(regular, "keep");
        Assert.False(File.Exists(Path.Combine(sensitive, "FILE")));
        Assert.True(File.Exists(Path.Combine(ordinary, "file")));
        RestoreNamePreflight.Validate(Destination, new[] { (first, false), (second, false), (regular, false) });
        Assert.Throws<InvalidDataException>(() => RestoreNamePreflight.Validate(Destination,
            new[] { (first, false), (second, false), (regular, false), (Path.Combine(ordinary, "file"), false) }));
        Assert.Equal("first", File.ReadAllText(first));
        Assert.Equal("second", File.ReadAllText(second));
        Assert.Equal("keep", File.ReadAllText(regular));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
