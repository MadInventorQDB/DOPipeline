
using Xunit;
using DifferentialBackup.Utilities;
using System.Collections.Generic;
using System.IO;
using System;
using System.Linq;

namespace DifferentialBackup.Test.Utilities
{
    public class DataPersistenceTests
    {
        [Fact]
        public void SaveAndLoadFileHashes_Success()
        {
            var fileHashes = new Dictionary<string, string>
            {
                { @"C:\path\to\file1.txt", "hash1" },
                { @"C:\path\to\file2.txt", "hash2" }
            };
            var filePath = Path.Combine(Path.GetTempPath(), "fileHashes.json");

            DataPersistence.SaveFileHashes(fileHashes, filePath);
            var loadedFileHashes = DataPersistence.LoadFileHashes(filePath);

            Assert.Equal(fileHashes.Count, loadedFileHashes.Count);
            foreach (var kvp in fileHashes)
            {
                Assert.True(loadedFileHashes.ContainsKey(kvp.Key));
                Assert.Equal(kvp.Value, loadedFileHashes[kvp.Key]);
            }

            File.Delete(filePath);
        }

        [Fact]
        public void LoadFileHashes_NonExistentFile_ReturnsEmptyDictionary()
        {
            var filePath = Path.Combine(Path.GetTempPath(), "nonexistentfile.json");

            var fileHashes = DataPersistence.LoadFileHashes(filePath);

            Assert.Empty(fileHashes);
        }

        [Fact]
        public void SaveAndLoadBackupDates_Success()
        {
            var backupDates = new HashSet<DateTime>
            {
                new DateTime(2022, 1, 1),
                new DateTime(2022, 1, 2)
            };
            var filePath = Path.Combine(Path.GetTempPath(), "backupDates.json");

            DataPersistence.SaveBackupDates(backupDates, filePath);
            var loadedBackupDates = DataPersistence.LoadBackupDates(filePath);

            Assert.Equal(backupDates.Count, loadedBackupDates.Count);
            foreach (var date in backupDates)
            {
                Assert.Contains(date, loadedBackupDates);
            }

            File.Delete(filePath);
        }

        [Fact]
        public void LoadBackupDates_NonExistentFile_ReturnsEmptyHashSet()
        {
            var filePath = Path.Combine(Path.GetTempPath(), "nonexistentfile.json");

            var backupDates = DataPersistence.LoadBackupDates(filePath);

            Assert.Empty(backupDates);
        }
    }
}
