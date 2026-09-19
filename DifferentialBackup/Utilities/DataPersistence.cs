using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace DifferentialBackup.Utilities
{
    public static class DataPersistence
    {
        public static ConcurrentDictionary<string, string> LoadFileHashes(string filePath)
        {
            if (File.Exists(filePath))
            {
                var json = File.ReadAllText(filePath);
                return JsonSerializer.Deserialize<ConcurrentDictionary<string, string>>(json) ?? new ConcurrentDictionary<string, string>();
            }
            return new ConcurrentDictionary<string, string>();
        }

        public static void SaveFileHashes(ConcurrentDictionary<string, string> fileHashes, string filePath)
        {
            var json = JsonSerializer.Serialize(fileHashes);
            WriteAllTextAtomic(filePath, json);
        }

        public static HashSet<DateTime> LoadBackupDates(string filePath)
        {
            if (File.Exists(filePath))
            {
                var json = File.ReadAllText(filePath);
                return JsonSerializer.Deserialize<HashSet<DateTime>>(json) ?? new HashSet<DateTime>();
            }
            return new HashSet<DateTime>();
        }

        public static void SaveBackupDates(HashSet<DateTime> backupDates, string filePath)
        {
            var json = JsonSerializer.Serialize(backupDates);
            WriteAllTextAtomic(filePath, json);
        }

        private static void WriteAllTextAtomic(string filePath, string contents)
        {
            var temporaryPath = filePath + ".tmp";
            File.WriteAllText(temporaryPath, contents);
            File.Move(temporaryPath, filePath, true);
        }
    }
}
