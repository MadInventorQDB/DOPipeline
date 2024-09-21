using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace DifferentialBackup.Utilities
{
    public static class DataPersistence
    {
        public static Dictionary<string, string> LoadFileHashes(string filePath)
        {
            if (File.Exists(filePath))
            {
                var json = File.ReadAllText(filePath);
                return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
            }
            return new Dictionary<string, string>();
        }

        public static void SaveFileHashes(Dictionary<string, string> fileHashes, string filePath)
        {
            var json = JsonSerializer.Serialize(fileHashes);
            File.WriteAllText(filePath, json);
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
            File.WriteAllText(filePath, json);
        }
    }
}
