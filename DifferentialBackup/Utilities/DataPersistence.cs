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
            if (!File.Exists(filePath))
            {
                return new ConcurrentDictionary<string, string>();
            }

            try
            {
                var value = JsonSerializer.Deserialize<ConcurrentDictionary<string, string>>(File.ReadAllText(filePath));
                return value ?? throw new InvalidDataException($"State file is null: '{filePath}'.");
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"State file is malformed: '{filePath}'.", ex);
            }
        }

        public static void SaveFileHashes(ConcurrentDictionary<string, string> fileHashes, string filePath)
        {
            var json = JsonSerializer.Serialize(fileHashes);
            WriteAllTextAtomic(filePath, json);
        }

        public static HashSet<DateTime> LoadBackupDates(string filePath)
        {
            if (!File.Exists(filePath))
            {
                return new HashSet<DateTime>();
            }

            try
            {
                var value = JsonSerializer.Deserialize<HashSet<DateTime>>(File.ReadAllText(filePath));
                return value ?? throw new InvalidDataException($"State file is null: '{filePath}'.");
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"State file is malformed: '{filePath}'.", ex);
            }
        }

        public static void SaveBackupDates(HashSet<DateTime> backupDates, string filePath)
        {
            var json = JsonSerializer.Serialize(backupDates);
            WriteAllTextAtomic(filePath, json);
        }

        private static void WriteAllTextAtomic(string filePath, string contents)
        {
            var temporaryPath = filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(stream))
                {
                    writer.Write(contents);
                    writer.Flush();
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporaryPath, filePath, true);
            }
            finally
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch
                {
                    // Cleanup must not replace the state-write error.
                }
            }
        }
    }
}
