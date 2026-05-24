using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Collections.Generic;
using System.Linq;

namespace DifferentialBackup.Utilities
{
    public class BackupRunState
    {
        private readonly object _lock = new();
        private readonly string _sourceDirectory;
        private readonly string _backupDestination;
        private readonly string _stagingDirectory;

        public BackupRunState(string sourceDirectory, string backupDestination, string? stagingRoot = null)
        {
            _sourceDirectory = Path.GetFullPath(sourceDirectory);
            _backupDestination = Path.GetFullPath(backupDestination);
            _stagingDirectory = Path.Combine(
                stagingRoot ?? GetDefaultStagingRoot(),
                CreateStableDirectoryName(_sourceDirectory, _backupDestination));
        }

        public BackupManifest? Manifest { get; private set; }

        public DateTime? BackupDate { get; private set; }

        public string StagingDirectory => _stagingDirectory;

        public string ManifestPath => Path.Combine(_stagingDirectory, "manifest.json");

        public string CompletedLogPath => Path.Combine(_stagingDirectory, "completed.log");

        public void BeginRun()
        {
            lock (_lock)
            {
                Manifest = LoadManifest();
                BackupDate = Manifest?.BackupDate;
            }
        }

        public DateTime GetOrCreateBackupDate(DateTime? preferredBackupDate = null)
        {
            lock (_lock)
            {
                BackupDate ??= preferredBackupDate.HasValue
                    ? TruncateToSecond(preferredBackupDate.Value)
                    : TruncateToSecond(DateTime.UtcNow);
                return BackupDate.Value;
            }
        }

        public BackupManifest CreateOrUpdateManifest(DateTime backupDate, IReadOnlyCollection<BackupManifestFile> files)
        {
            lock (_lock)
            {
                Directory.CreateDirectory(_stagingDirectory);

                Manifest ??= new BackupManifest
                {
                    SourceDirectory = _sourceDirectory,
                    BackupDestination = _backupDestination,
                    BackupDate = backupDate,
                    PartialArchiveName = backupDate.ToString("yyyyMMddHHmmss") + ".zip.partial",
                    StagingArchivePath = Path.Combine(_stagingDirectory, backupDate.ToString("yyyyMMddHHmmss") + ".zip.partial")
                };

                MergeManifestFiles(Manifest, files);
                SaveManifest();

                return Manifest;
            }
        }

        public Dictionary<int, string> LoadCompletedFiles()
        {
            lock (_lock)
            {
                var completed = new Dictionary<int, string>();
                if (!File.Exists(CompletedLogPath))
                {
                    return completed;
                }

                foreach (var line in File.ReadLines(CompletedLogPath))
                {
                    var parts = line.Split('\t');
                    if (parts.Length >= 2 && int.TryParse(parts[0], out var index))
                    {
                        completed[index] = parts[1];
                    }
                }

                return completed;
            }
        }

        public void AppendCompletedFile(int index, string hash)
        {
            lock (_lock)
            {
                Directory.CreateDirectory(_stagingDirectory);
                File.AppendAllText(CompletedLogPath, $"{index}\t{hash}{Environment.NewLine}");
            }
        }

        public void ClearCompletedLog()
        {
            lock (_lock)
            {
                if (File.Exists(CompletedLogPath))
                {
                    File.Delete(CompletedLogPath);
                }
            }
        }

        public void ClearRun()
        {
            lock (_lock)
            {
                Manifest = null;
                if (Directory.Exists(_stagingDirectory))
                {
                    Directory.Delete(_stagingDirectory, true);
                }
            }
        }

        private BackupManifest? LoadManifest()
        {
            try
            {
                if (!File.Exists(ManifestPath))
                {
                    return null;
                }

                var json = File.ReadAllText(ManifestPath);
                var manifest = JsonSerializer.Deserialize<BackupManifest>(json);
                if (manifest == null ||
                    !PathsEqual(manifest.SourceDirectory, _sourceDirectory) ||
                    !PathsEqual(manifest.BackupDestination, _backupDestination))
                {
                    return null;
                }

                if (string.IsNullOrWhiteSpace(manifest.StagingArchivePath))
                {
                    manifest.StagingArchivePath = Path.Combine(_stagingDirectory, manifest.PartialArchiveName);
                }

                return manifest;
            }
            catch
            {
                return null;
            }
        }

        private void SaveManifest()
        {
            if (Manifest == null)
            {
                return;
            }

            var json = JsonSerializer.Serialize(Manifest);
            File.WriteAllText(ManifestPath, json);
        }

        private static void MergeManifestFiles(BackupManifest manifest, IReadOnlyCollection<BackupManifestFile> files)
        {
            var byEntry = manifest.Files.ToDictionary(file => file.EntryName, StringComparer.OrdinalIgnoreCase);
            var nextIndex = manifest.Files.Count == 0 ? 0 : manifest.Files.Max(file => file.Index) + 1;

            foreach (var file in files)
            {
                if (byEntry.TryGetValue(file.EntryName, out var existing))
                {
                    existing.SourcePath = file.SourcePath;
                    existing.Hash = file.Hash;
                    existing.Length = file.Length;
                    continue;
                }

                file.Index = nextIndex++;
                manifest.Files.Add(file);
                byEntry[file.EntryName] = file;
            }
        }

        private static DateTime TruncateToSecond(DateTime value)
        {
            return new DateTime(value.Ticks - value.Ticks % TimeSpan.TicksPerSecond, value.Kind);
        }

        private static bool PathsEqual(string left, string right)
        {
            return string.Equals(
                Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }

        private static string GetDefaultStagingRoot()
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, "DifferentialBackup", "Staging");
        }

        private static string CreateStableDirectoryName(string sourceDirectory, string backupDestination)
        {
            var key = $"{sourceDirectory}|{backupDestination}";
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).Substring(0, 16);
            return hash;
        }
    }
}
