using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using DifferentialBackup.Utilities;
using Xunit;

namespace DifferentialBackup.Test.Systems;

public sealed partial class BackupRecoveryRegressionTests
{
    [Theory]
    [InlineData("journal-job-updated-flushed", false)]
    [InlineData("part-reservation-committed", false)]
    [InlineData("part-plan-committed", false)]
    [InlineData("staged-output-closed", false)]
    [InlineData("journal-part-plan-flushed", false)]
    [InlineData("journal-part-checkpoint-flushed", true)]
    [InlineData("part-checkpoint-committed", true)]
    [InlineData("destination-archive-renamed", true)]
    [InlineData("part-transfer-committed", true)]
    [InlineData("journal-part-transferred-flushed", true)]
    [InlineData("journal-publication-receipt-flushed", true)]
    [InlineData("publication-receipt-committed", true)]
    [InlineData("publication-directory-renamed", true)]
    [InlineData("hash-state-replaced", true)]
    [InlineData("date-state-replaced", true)]
    [InlineData("state-files-saved-before-complete", true)]
    [InlineData("state-commit-Complete", true)]
    public async Task KilledProcessReusesDurableBytesAndCommitsState(string boundary, bool acknowledged)
    {
        var p = RepairPaths();
        var file = Path.Combine(p.Source, "file.txt");
        File.WriteAllText(file, "AAAA");
        var expectedHash = Hash(file);
        using (var first = RepairChild(p, boundary))
        {
            try
            {
                Assert.Equal("CHECKPOINT:" + boundary,
                    await first.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20)));
                Assert.Empty(Directory.GetFiles(p.Destination, "*.report.json"));
            }
            finally { await KillChild(first); }
        }
        if (acknowledged) File.Delete(file); // zero possible source recapture
        using (var second = RepairChild(p))
        {
            try
            {
                await second.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
                Assert.True(second.ExitCode == 0, await second.StandardError.ReadToEndAsync());
            }
            finally { await KillChild(second); }
        }
        var set = Assert.Single(Directory.GetDirectories(p.Destination, "*.backup"));
        var archives = Directory.GetFiles(set, "part-*.zip");
        var archivePath = Assert.Single(archives);
        using var zip = ZipFile.OpenRead(archivePath);
        var entry = Assert.Single(zip.Entries);
        Assert.Equal("file.txt", entry.FullName);
        using var bytes = entry.Open();
        Assert.Equal(expectedHash, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        Assert.Equal(expectedHash, DataPersistence.LoadFileHashes(p.Hashes)[file]);
        Assert.Single(DataPersistence.LoadBackupDates(p.Dates));
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(set, "manifest.json")));
        var part = manifest.RootElement.GetProperty("Parts")[0];
        Assert.Equal(new FileInfo(archivePath).Length, part.GetProperty("ArchiveBytes").GetInt64());
        Assert.Equal(Hash(archivePath), part.GetProperty("ArchiveSha256").GetString()!.ToLowerInvariant());
        var indexPath = Path.Combine(set, part.GetProperty("IndexFileName").GetString()!);
        Assert.Equal(new FileInfo(indexPath).Length, part.GetProperty("IndexBytes").GetInt64());
        Assert.Equal(Hash(indexPath), part.GetProperty("IndexSha256").GetString()!.ToLowerInvariant());
        using var index = JsonDocument.Parse(File.ReadAllText(indexPath));
        var descriptor = index.RootElement.GetProperty("Files")[0];
        Assert.Equal(expectedHash, descriptor.GetProperty("Hash").GetString()!.ToLowerInvariant());
        Assert.Equal(4, descriptor.GetProperty("Length").GetInt64());
        if (boundary == "part-reservation-committed") Assert.Equal(2, part.GetProperty("PartNumber").GetInt32());
        Assert.Single(Directory.GetFiles(p.Destination, "*.report.json"));
    }

    [Fact]
    public async Task KilledTransferAfterOmissionPreservesGapAndAllocatesNewPart()
    {
        var p = RepairPaths();
        var a = Path.Combine(p.Source, "a.txt");
        var b = Path.Combine(p.Source, "b.txt");
        File.WriteAllText(a, "AAAA");
        File.WriteAllText(b, "BBBB");
        using (var first = RepairChild(p, "part-transfer-committed", a))
        {
            try { Assert.Equal("CHECKPOINT:part-transfer-committed", await first.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20))); }
            finally { await KillChild(first); }
        }
        File.WriteAllText(a, "AAAA");
        File.Delete(b);
        using (var second = RepairChild(p))
        {
            try
            {
                await second.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
                Assert.True(second.ExitCode == 0, await second.StandardError.ReadToEndAsync());
            }
            finally { await KillChild(second); }
        }
        var set = Assert.Single(Directory.GetDirectories(p.Destination, "*.backup"));
        Assert.Equal(new[] { "part-000002.zip", "part-000003.zip" },
            Directory.GetFiles(set, "*.zip").Select(Path.GetFileName).OrderBy(name => name));
        Assert.Equal("BBBB", ReadEntry(Path.Combine(set, "part-000002.zip"), "b.txt"));
        Assert.Equal("AAAA", ReadEntry(Path.Combine(set, "part-000003.zip"), "a.txt"));
        Assert.Single(DataPersistence.LoadBackupDates(p.Dates));
    }

    [Fact]
    public async Task KilledMultiPartMidStagingCleansPartialArchiveAndCompletesAllParts()
    {
        var p = RepairPaths();
        var state = new BackupRunState(p.Source, p.Destination, p.Staging);
        var a = Path.Combine(p.Source, "a.txt");
        var b = Path.Combine(p.Source, "b.txt");
        var c = Path.Combine(p.Source, "c.txt");
        File.WriteAllText(a, "AAAA");
        File.WriteAllText(b, "BBBB");
        File.WriteAllText(c, "CCCC");
        var hashA = Hash(a);
        var hashB = Hash(b);
        var hashC = Hash(c);

        using (var first = RepairChild(p, "part-transfer-committed"))
        {
            try
            {
                Assert.Equal("CHECKPOINT:part-transfer-committed",
                    await first.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20)));
            }
            finally { await KillChild(first); }
        }

        var partialPath = Path.Combine(state.StagingDirectory, "part-000002.zip.partial");
        File.WriteAllText(partialPath, "corrupted incomplete bytes");
        File.Delete(a);

        using (var second = RepairChild(p))
        {
            try
            {
                await second.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
                Assert.True(second.ExitCode == 0, await second.StandardError.ReadToEndAsync());
            }
            finally { await KillChild(second); }
        }

        Assert.False(File.Exists(partialPath));
        var set = Assert.Single(Directory.GetDirectories(p.Destination, "*.backup"));
        var zipFiles = Directory.GetFiles(set, "part-*.zip").OrderBy(name => name).ToList();
        Assert.Equal(3, zipFiles.Count);
        Assert.Equal("AAAA", ReadEntry(zipFiles[0], "a.txt"));
        Assert.Equal("BBBB", ReadEntry(zipFiles[1], "b.txt"));
        Assert.Equal("CCCC", ReadEntry(zipFiles[2], "c.txt"));
        Assert.Equal(hashA, DataPersistence.LoadFileHashes(p.Hashes)[a]);
        Assert.Equal(hashB, DataPersistence.LoadFileHashes(p.Hashes)[b]);
        Assert.Equal(hashC, DataPersistence.LoadFileHashes(p.Hashes)[c]);
        Assert.Single(DataPersistence.LoadBackupDates(p.Dates));
    }

    [Fact]
    public async Task KilledProcessWithTruncatedJournalTailRecoversAndFinishes()
    {
        var p = RepairPaths();
        var state = new BackupRunState(p.Source, p.Destination, p.Staging);
        var file = Path.Combine(p.Source, "file.txt");
        File.WriteAllText(file, "AAAA");
        var expectedHash = Hash(file);

        using (var first = RepairChild(p, "part-plan-committed"))
        {
            try
            {
                Assert.Equal("CHECKPOINT:part-plan-committed",
                    await first.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20)));
            }
            finally { await KillChild(first); }
        }

        var journalPath = state.JournalPath;
        Assert.True(File.Exists(journalPath));
        File.AppendAllText(journalPath, "{\"Sequence\":999,\"ChangeType\":\"truncated");

        using (var second = RepairChild(p))
        {
            try
            {
                await second.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
                Assert.True(second.ExitCode == 0, await second.StandardError.ReadToEndAsync());
            }
            finally { await KillChild(second); }
        }

        var set = Assert.Single(Directory.GetDirectories(p.Destination, "*.backup"));
        var archive = Assert.Single(Directory.GetFiles(set, "part-*.zip"));
        Assert.Equal("AAAA", ReadEntry(archive, "file.txt"));
        Assert.Equal(expectedHash, DataPersistence.LoadFileHashes(p.Hashes)[file]);
        Assert.Single(DataPersistence.LoadBackupDates(p.Dates));
    }

    [Fact]
    public async Task KilledProcessWithCorruptedMiddleJournalRecordFailsFatallyAndPreservesArtifacts()
    {
        var p = RepairPaths();
        var state = new BackupRunState(p.Source, p.Destination, p.Staging);
        var file = Path.Combine(p.Source, "file.txt");
        File.WriteAllText(file, "AAAA");

        using (var first = RepairChild(p, "part-checkpoint-committed"))
        {
            try
            {
                Assert.Equal("CHECKPOINT:part-checkpoint-committed",
                    await first.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20)));
            }
            finally { await KillChild(first); }
        }

        var journalPath = state.JournalPath;
        Assert.True(File.Exists(journalPath));
        var lines = File.ReadAllLines(journalPath);
        Assert.True(lines.Length >= 2);
        lines[0] = lines[0].Replace("\"job-updated\"", "\"tampered-type\"");
        File.WriteAllLines(journalPath, lines);

        var stagingSnapshot = Directory.GetFiles(state.StagingDirectory).ToDictionary(path => path, File.ReadAllBytes);

        using (var second = RepairChild(p))
        {
            try
            {
                await second.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
                Assert.Equal(2, second.ExitCode);
                var err = await second.StandardError.ReadToEndAsync();
                Assert.Contains("journal", err, StringComparison.OrdinalIgnoreCase);
            }
            finally { await KillChild(second); }
        }

        foreach (var pair in stagingSnapshot)
        {
            Assert.True(File.Exists(pair.Key));
            Assert.Equal(pair.Value, File.ReadAllBytes(pair.Key));
        }
    }

    [Fact]
    public async Task KilledProcessDuringPublicationManifestCopyRecoversIdempotently()
    {
        var p = RepairPaths();
        var file = Path.Combine(p.Source, "file.txt");
        File.WriteAllText(file, "AAAA");
        var expectedHash = Hash(file);

        using (var first = RepairChild(p, "publication-receipt-committed"))
        {
            try
            {
                Assert.Equal("CHECKPOINT:publication-receipt-committed",
                    await first.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20)));
            }
            finally { await KillChild(first); }
        }

        var copyingDir = Assert.Single(Directory.GetDirectories(p.Destination, "*.backup.copying"));
        var uncompletedManifestCopy = Path.Combine(copyingDir, "manifest.json.test.copying");
        File.WriteAllText(uncompletedManifestCopy, "partial manifest copy data");
        File.Delete(file);

        using (var second = RepairChild(p))
        {
            try
            {
                await second.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
                Assert.True(second.ExitCode == 0, await second.StandardError.ReadToEndAsync());
            }
            finally { await KillChild(second); }
        }

        var set = Assert.Single(Directory.GetDirectories(p.Destination, "*.backup"));
        var archive = Assert.Single(Directory.GetFiles(set, "part-*.zip"));
        Assert.Equal("AAAA", ReadEntry(archive, "file.txt"));
        Assert.Equal(expectedHash, DataPersistence.LoadFileHashes(p.Hashes)[file]);
        Assert.Single(DataPersistence.LoadBackupDates(p.Dates));
    }

    [Fact]
    public async Task KilledRestoreProcessSkipsVerifiedTargetsAndRestoresRemainingEntries()
    {
        var p = RepairPaths();
        var f1 = Path.Combine(p.Source, "doc1.txt");
        var f2 = Path.Combine(p.Source, "doc2.txt");
        File.WriteAllText(f1, "DOC_ONE_CONTENT");
        File.WriteAllText(f2, "DOC_TWO_CONTENT");

        using (var backupProcess = RepairChild(p))
        {
            try
            {
                await backupProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
                Assert.True(backupProcess.ExitCode == 0, await backupProcess.StandardError.ReadToEndAsync());
            }
            finally { await KillChild(backupProcess); }
        }

        var backupSet = Assert.Single(Directory.GetDirectories(p.Destination, "*.backup"));
        var dateString = Path.GetFileName(backupSet).Replace(".backup", "");
        var restoreRoot = Path.Combine(_root, "restore");
        Directory.CreateDirectory(restoreRoot);

        using (var firstRestore = RepairRestoreChild(p.Destination, restoreRoot, dateString, "restore-entry-restored"))
        {
            try
            {
                Assert.Equal("CHECKPOINT:restore-entry-restored",
                    await firstRestore.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20)));
            }
            finally { await KillChild(firstRestore); }
        }

        var restoredDoc1 = Path.Combine(restoreRoot, "doc1.txt");
        var restoredDoc2 = Path.Combine(restoreRoot, "doc2.txt");
        Assert.True(File.Exists(restoredDoc1) ^ File.Exists(restoredDoc2));

        var alreadyRestored = File.Exists(restoredDoc1) ? restoredDoc1 : restoredDoc2;
        var originalTimestamp = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(alreadyRestored, originalTimestamp);

        using (var secondRestore = RepairRestoreChild(p.Destination, restoreRoot, dateString))
        {
            try
            {
                await secondRestore.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
                Assert.True(secondRestore.ExitCode == 0, await secondRestore.StandardError.ReadToEndAsync());
            }
            finally { await KillChild(secondRestore); }
        }

        Assert.True(File.Exists(restoredDoc1));
        Assert.True(File.Exists(restoredDoc2));
        Assert.Equal("DOC_ONE_CONTENT", File.ReadAllText(restoredDoc1));
        Assert.Equal("DOC_TWO_CONTENT", File.ReadAllText(restoredDoc2));
        Assert.Equal(originalTimestamp, File.GetLastWriteTimeUtc(alreadyRestored));
    }

    [Fact]
    public async Task KilledRestoreProcessBetweenTargetReplacedAndJournalCommittedRecovers()
    {
        var p = RepairPaths();
        var f1 = Path.Combine(p.Source, "doc1.txt");
        var f2 = Path.Combine(p.Source, "doc2.txt");
        File.WriteAllText(f1, "DOC_ONE_CONTENT");
        File.WriteAllText(f2, "DOC_TWO_CONTENT");

        using (var backupProcess = RepairChild(p))
        {
            try
            {
                await backupProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
                Assert.True(backupProcess.ExitCode == 0, await backupProcess.StandardError.ReadToEndAsync());
            }
            finally { await KillChild(backupProcess); }
        }

        var backupSet = Assert.Single(Directory.GetDirectories(p.Destination, "*.backup"));
        var dateString = Path.GetFileName(backupSet).Replace(".backup", "");
        var restoreRoot = Path.Combine(_root, "restore-replace-test");
        Directory.CreateDirectory(restoreRoot);

        using (var firstRestore = RepairRestoreChild(p.Destination, restoreRoot, dateString, "restore-target-replaced"))
        {
            try
            {
                Assert.Equal("CHECKPOINT:restore-target-replaced",
                    await firstRestore.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20)));
            }
            finally { await KillChild(firstRestore); }
        }

        var restoredDoc1 = Path.Combine(restoreRoot, "doc1.txt");
        var restoredDoc2 = Path.Combine(restoreRoot, "doc2.txt");
        Assert.True(File.Exists(restoredDoc1) || File.Exists(restoredDoc2));

        using (var secondRestore = RepairRestoreChild(p.Destination, restoreRoot, dateString))
        {
            try
            {
                await secondRestore.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
                Assert.True(secondRestore.ExitCode == 0, await secondRestore.StandardError.ReadToEndAsync());
            }
            finally { await KillChild(secondRestore); }
        }

        Assert.True(File.Exists(restoredDoc1));
        Assert.True(File.Exists(restoredDoc2));
        Assert.Equal("DOC_ONE_CONTENT", File.ReadAllText(restoredDoc1));
        Assert.Equal("DOC_TWO_CONTENT", File.ReadAllText(restoredDoc2));
    }

    [Fact]
    public async Task KilledRestoreProcessDuringRetryWaitRecoversAndFinishes()
    {
        var p = RepairPaths();
        var f1 = Path.Combine(p.Source, "doc1.txt");
        var f2 = Path.Combine(p.Source, "doc2.txt");
        File.WriteAllText(f1, "DOC_ONE_CONTENT");
        File.WriteAllText(f2, "DOC_TWO_CONTENT");

        using (var backupProcess = RepairChild(p))
        {
            try
            {
                await backupProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
                Assert.True(backupProcess.ExitCode == 0, await backupProcess.StandardError.ReadToEndAsync());
            }
            finally { await KillChild(backupProcess); }
        }

        var backupSet = Assert.Single(Directory.GetDirectories(p.Destination, "*.backup"));
        var dateString = Path.GetFileName(backupSet).Replace(".backup", "");
        var restoreRoot = Path.Combine(_root, "restore-wait-test");
        Directory.CreateDirectory(restoreRoot);

        var lockedDoc = Path.Combine(restoreRoot, "doc2.txt");
        File.WriteAllText(lockedDoc, "LOCKED_PREEXISTING");
        using (var lockStream = new FileStream(lockedDoc, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            using (var firstRestore = RepairRestoreChild(p.Destination, restoreRoot, dateString, "restore-retry-waiting"))
            {
                try
                {
                    Assert.Equal("CHECKPOINT:restore-retry-waiting",
                        await firstRestore.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20)));
                }
                finally { await KillChild(firstRestore); }
            }
        }

        using (var secondRestore = RepairRestoreChild(p.Destination, restoreRoot, dateString))
        {
            try
            {
                await secondRestore.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
                Assert.True(secondRestore.ExitCode == 0, await secondRestore.StandardError.ReadToEndAsync());
            }
            finally { await KillChild(secondRestore); }
        }

        var restoredDoc1 = Path.Combine(restoreRoot, "doc1.txt");
        var restoredDoc2 = Path.Combine(restoreRoot, "doc2.txt");
        Assert.True(File.Exists(restoredDoc1));
        Assert.True(File.Exists(restoredDoc2));
        Assert.Equal("DOC_ONE_CONTENT", File.ReadAllText(restoredDoc1));
        Assert.Equal("DOC_TWO_CONTENT", File.ReadAllText(restoredDoc2));
    }

    [Fact]
    public async Task KilledRestoreProcessDuringInterruptedRetryRoundRecoversAndFinishes()
    {
        var p = RepairPaths();
        var f1 = Path.Combine(p.Source, "doc1.txt");
        var f2 = Path.Combine(p.Source, "doc2.txt");
        var f3 = Path.Combine(p.Source, "doc3.txt");
        File.WriteAllText(f1, "DOC_ONE_CONTENT");
        File.WriteAllText(f2, "DOC_TWO_CONTENT");
        File.WriteAllText(f3, "DOC_THREE_CONTENT");

        using (var backupProcess = RepairChild(p))
        {
            try
            {
                await backupProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
                Assert.True(backupProcess.ExitCode == 0, await backupProcess.StandardError.ReadToEndAsync());
            }
            finally { await KillChild(backupProcess); }
        }

        var backupSet = Assert.Single(Directory.GetDirectories(p.Destination, "*.backup"));
        var dateString = Path.GetFileName(backupSet).Replace(".backup", "");
        var restoreRoot = Path.Combine(_root, "restore-interrupted-test");
        Directory.CreateDirectory(restoreRoot);

        var lockedDoc2 = Path.Combine(restoreRoot, "doc2.txt");
        var lockedDoc3 = Path.Combine(restoreRoot, "doc3.txt");
        File.WriteAllText(lockedDoc2, "LOCKED_PREEXISTING_2");
        File.WriteAllText(lockedDoc3, "LOCKED_PREEXISTING_3");
        using (var lockStream2 = new FileStream(lockedDoc2, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        using (var lockStream3 = new FileStream(lockedDoc3, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            using (var firstRestore = RepairRestoreChild(p.Destination, restoreRoot, dateString, "restore-retry-waiting"))
            {
                try
                {
                    Assert.Equal("CHECKPOINT:restore-retry-waiting",
                        await firstRestore.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20)));
                }
                finally { await KillChild(firstRestore); }
            }
        }

        // doc2 is unlocked, but doc3 remains locked.
        // secondRestore will restore doc2, hit restore-entry-restored, and be killed mid-round!
        using (var lockStream3 = new FileStream(lockedDoc3, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            using (var secondRestore = RepairRestoreChild(p.Destination, restoreRoot, dateString, "restore-entry-restored"))
            {
                try
                {
                    Assert.Equal("CHECKPOINT:restore-entry-restored",
                        await secondRestore.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20)));
                }
                finally { await KillChild(secondRestore); }
            }
        }

        // Verify doc2 was completed and acknowledged before the crash
        Assert.True(File.Exists(lockedDoc2));
        Assert.Equal("DOC_TWO_CONTENT", File.ReadAllText(lockedDoc2));
        var journal = RestoreJournal.Load(restoreRoot);
        Assert.Contains(journal.Successes, s => s.Key.EndsWith("doc2.txt"));

        // Set doc2 to read-only so any redundant attempt to re-restore would fail
        File.SetAttributes(lockedDoc2, FileAttributes.ReadOnly);

        try
        {
            using (var thirdRestore = RepairRestoreChild(p.Destination, restoreRoot, dateString))
            {
                try
                {
                    await thirdRestore.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
                    Assert.True(thirdRestore.ExitCode == 0, await thirdRestore.StandardError.ReadToEndAsync());
                }
                finally { await KillChild(thirdRestore); }
            }
        }
        finally
        {
            File.SetAttributes(lockedDoc2, FileAttributes.Normal);
        }

        var restoredDoc1 = Path.Combine(restoreRoot, "doc1.txt");
        var restoredDoc2 = Path.Combine(restoreRoot, "doc2.txt");
        var restoredDoc3 = Path.Combine(restoreRoot, "doc3.txt");
        Assert.True(File.Exists(restoredDoc1));
        Assert.True(File.Exists(restoredDoc2));
        Assert.True(File.Exists(restoredDoc3));
        Assert.Equal("DOC_ONE_CONTENT", File.ReadAllText(restoredDoc1));
        Assert.Equal("DOC_TWO_CONTENT", File.ReadAllText(restoredDoc2));
        Assert.Equal("DOC_THREE_CONTENT", File.ReadAllText(restoredDoc3));
    }

    [Fact]
    public async Task KilledBackupProcessDuringRetryWaitRecoversAndFinishes()
    {
        var p = RepairPaths();
        var f1 = Path.Combine(p.Source, "file1.txt");
        var f2 = Path.Combine(p.Source, "file2.txt");
        File.WriteAllText(f1, "FILE_ONE_CONTENT");
        File.WriteAllText(f2, "FILE_TWO_CONTENT");

        using (var lockStream = new FileStream(f2, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            using (var firstBackup = RepairChild(p, "backup-retry-waiting"))
            {
                try
                {
                    Assert.Equal("CHECKPOINT:backup-retry-waiting",
                        await firstBackup.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20)));
                }
                finally { await KillChild(firstBackup); }
            }
        }

        // Resume after unlocking f2
        using (var secondBackup = RepairChild(p))
        {
            try
            {
                await secondBackup.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
                Assert.True(secondBackup.ExitCode == 0, await secondBackup.StandardError.ReadToEndAsync());
            }
            finally { await KillChild(secondBackup); }
        }

        var backupSet = Assert.Single(Directory.GetDirectories(p.Destination, "*.backup"));
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(backupSet, "manifest.json")));
        Assert.True(manifest.RootElement.GetProperty("IsComplete").GetBoolean());
        Assert.Single(Directory.GetFiles(p.Destination, "*.report.json"));
    }

    [Fact]
    public async Task KilledBackupProcessDuringMidRetryRoundRecoversAndFinishes()
    {
        var p = RepairPaths();
        var f1 = Path.Combine(p.Source, "file1.txt");
        var f2 = Path.Combine(p.Source, "file2.txt");
        var f3 = Path.Combine(p.Source, "file3.txt");
        File.WriteAllText(f1, "FILE_ONE_CONTENT");
        File.WriteAllText(f2, "FILE_TWO_CONTENT");
        File.WriteAllText(f3, "FILE_THREE_CONTENT");

        using (var lockStream2 = new FileStream(f2, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        using (var lockStream3 = new FileStream(f3, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            using (var firstBackup = RepairChild(p, "backup-retry-waiting"))
            {
                try
                {
                    Assert.Equal("CHECKPOINT:backup-retry-waiting",
                        await firstBackup.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20)));
                }
                finally { await KillChild(firstBackup); }
            }
        }

        // f2 is unlocked, but f3 remains locked.
        // second backup captures f2 into part 2, transfers it, and is killed mid-round!
        using (var lockStream3 = new FileStream(f3, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            using (var secondBackup = RepairChild(p, "part-transfer-committed"))
            {
                try
                {
                    Assert.Equal("CHECKPOINT:part-transfer-committed",
                        await secondBackup.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20)));
                }
                finally { await KillChild(secondBackup); }
            }
        }

        // Unlock f3 and run final recovery
        using (var thirdBackup = RepairChild(p))
        {
            try
            {
                await thirdBackup.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
                Assert.True(thirdBackup.ExitCode == 0, await thirdBackup.StandardError.ReadToEndAsync());
            }
            finally { await KillChild(thirdBackup); }
        }

        var backupSet = Assert.Single(Directory.GetDirectories(p.Destination, "*.backup"));
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(backupSet, "manifest.json")));
        Assert.True(manifest.RootElement.GetProperty("IsComplete").GetBoolean());
        Assert.Single(Directory.GetFiles(p.Destination, "*.report.json"));
    }

    private Process RepairRestoreChild(string backupDestination, string restoreDestination, string backupDate,
        string boundary = "")
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var arg in new[] { Path.Combine(AppContext.BaseDirectory, "DifferentialBackup.TestHost.dll"),
            "restore", backupDestination, restoreDestination, backupDate, boundary,
            Path.Combine(_root, "restore-signal"), Path.Combine(_root, "restore-release") })
            start.ArgumentList.Add(arg);
        return Process.Start(start)!;
    }

    private Process RepairChild((string Source, string Destination, string Staging, string Hashes, string Dates) p,
        string boundary = "", string omit = "", string mode = "")
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var arg in new[] { Path.Combine(AppContext.BaseDirectory, "DifferentialBackup.TestHost.dll"),
            "backup", p.Source, p.Destination, p.Staging, p.Hashes, p.Dates, boundary,
            Path.Combine(_root, "signal"), Path.Combine(_root, "release"), omit, mode })
            start.ArgumentList.Add(arg);
        return Process.Start(start)!;
    }

    private static async Task KillChild(Process child)
    {
        if (!child.HasExited) child.Kill(entireProcessTree: true);
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }
}
