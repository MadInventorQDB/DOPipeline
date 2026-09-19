using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DifferentialBackup.Utilities
{
    public sealed class BackupRunState
    {
        private readonly object _lock = new();
        private readonly string _sourceDirectory;
        private readonly string _backupDestination;
        private readonly string _stagingRoot;
        private readonly string _stagingDirectory;

        public BackupRunState(string sourceDirectory, string backupDestination, string? stagingRoot = null)
        {
            _sourceDirectory = NormalizeDirectory(sourceDirectory);
            _backupDestination = NormalizeDirectory(backupDestination);
            _stagingRoot = NormalizeDirectory(stagingRoot ?? GetDefaultStagingRoot());
            _stagingDirectory = Path.Combine(
                _stagingRoot,
                CreateStableDirectoryName(_sourceDirectory, _backupDestination));
        }

        public BackupJobState? Job { get; private set; }

        public DateTime? BackupDate { get; private set; }

        public string StagingDirectory => _stagingDirectory;

        public string JobPath => Path.Combine(_stagingDirectory, "job.json");

        public string FinalManifestStagingPath => Path.Combine(_stagingDirectory, "manifest.json");

        public void BeginRun()
        {
            lock (_lock)
            {
                Job = null;
                BackupDate = null;

                if (!Directory.Exists(_stagingDirectory))
                {
                    return;
                }

                Job = LoadJob();
                if (Job == null)
                {
                    throw new InvalidDataException(
                        $"Staging directory does not contain a compatible backup job: '{_stagingDirectory}'. " +
                        "Remove or relocate it before retrying.");
                }

                BackupDate = Job.BackupDate;
            }
        }

        public DateTime GetOrCreateBackupDate(DateTime? preferredBackupDate = null)
        {
            lock (_lock)
            {
                BackupDate ??= TruncateToSecond(preferredBackupDate ?? DateTime.UtcNow);
                return BackupDate.Value;
            }
        }

        public BackupJobState CreateOrUpdateJob(
            DateTime backupDate,
            BackupBatchOptions options,
            string planFingerprint,
            int totalParts,
            int totalFiles,
            long sourceBytes)
        {
            lock (_lock)
            {
                Directory.CreateDirectory(_stagingDirectory);
                var preservePublishedState = Job is { Published: true } &&
                    string.Equals(Job.PlanFingerprint, planFingerprint, StringComparison.Ordinal);

                Job ??= new BackupJobState
                {
                    SourceDirectory = _sourceDirectory,
                    BackupDestination = _backupDestination,
                    BackupDate = TruncateToSecond(backupDate)
                };

                Job.MaxSourceBytesPerPart = options.MaxSourceBytesPerPart;
                Job.MaxFilesPerPart = options.MaxFilesPerPart;
                Job.PlanFingerprint = planFingerprint;
                Job.TotalParts = totalParts;
                Job.TotalFiles = totalFiles;
                Job.SourceBytes = sourceBytes;
                Job.Published = preservePublishedState;
                BackupDate = Job.BackupDate;
                SaveJsonAtomic(JobPath, Job);

                return Job;
            }
        }

        public BackupPartCheckpoint? LoadPartCheckpoint(int partNumber)
        {
            lock (_lock)
            {
                var path = GetPartCheckpointPath(partNumber);
                try
                {
                    if (!File.Exists(path))
                    {
                        return null;
                    }

                    var checkpoint = JsonSerializer.Deserialize<BackupPartCheckpoint>(File.ReadAllText(path));
                    return checkpoint is { FormatVersion: BackupJobState.CurrentFormatVersion } &&
                           checkpoint.PartNumber == partNumber
                        ? checkpoint
                        : null;
                }
                catch
                {
                    return null;
                }
            }
        }

        public void SavePartCheckpoint(BackupPartCheckpoint checkpoint)
        {
            lock (_lock)
            {
                Directory.CreateDirectory(_stagingDirectory);
                SaveJsonAtomic(GetPartCheckpointPath(checkpoint.PartNumber), checkpoint);
            }
        }

        public void DeletePartCheckpoint(int partNumber)
        {
            lock (_lock)
            {
                File.Delete(GetPartCheckpointPath(partNumber));
            }
        }

        public void MarkPublished()
        {
            lock (_lock)
            {
                if (Job == null)
                {
                    throw new InvalidOperationException("Backup job was not initialized.");
                }

                Job.Published = true;
                SaveJsonAtomic(JobPath, Job);
            }
        }

        public void ResetRun()
        {
            lock (_lock)
            {
                Job = null;
                BackupDate = null;
                DeleteStagingDirectory();
            }
        }

        public void ClearRun()
        {
            ResetRun();
        }

        public string GetPartCheckpointPath(int partNumber)
        {
            return Path.Combine(_stagingDirectory, $"part-{partNumber:000000}.index.json");
        }

        private BackupJobState? LoadJob()
        {
            try
            {
                if (!File.Exists(JobPath))
                {
                    return null;
                }

                var job = JsonSerializer.Deserialize<BackupJobState>(File.ReadAllText(JobPath));
                if (job == null ||
                    job.FormatVersion != BackupJobState.CurrentFormatVersion ||
                    !PathsEqual(job.SourceDirectory, _sourceDirectory) ||
                    !PathsEqual(job.BackupDestination, _backupDestination))
                {
                    return null;
                }

                return job;
            }
            catch
            {
                return null;
            }
        }

        private void DeleteStagingDirectory()
        {
            if (!Directory.Exists(_stagingDirectory))
            {
                return;
            }

            var parent = Path.GetDirectoryName(_stagingDirectory);
            if (!PathsEqual(parent ?? string.Empty, _stagingRoot))
            {
                throw new InvalidOperationException("Refusing to remove a staging directory outside the staging root.");
            }

            Directory.Delete(_stagingDirectory, true);
        }

        private static void SaveJsonAtomic<T>(string path, T value)
        {
            var temporaryPath = path + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(value));
            File.Move(temporaryPath, path, true);
        }

        private static DateTime TruncateToSecond(DateTime value)
        {
            return new DateTime(value.Ticks - value.Ticks % TimeSpan.TicksPerSecond, value.Kind);
        }

        private static string NormalizeDirectory(string path)
        {
            var fullPath = Path.GetFullPath(path);
            var root = Path.GetPathRoot(fullPath);
            return string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase)
                ? fullPath
                : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static bool PathsEqual(string left, string right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            {
                return false;
            }

            return string.Equals(NormalizeDirectory(left), NormalizeDirectory(right), StringComparison.OrdinalIgnoreCase);
        }

        private static string GetDefaultStagingRoot()
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, "DifferentialBackup", "Staging");
        }

        private static string CreateStableDirectoryName(string sourceDirectory, string backupDestination)
        {
            var key = $"{sourceDirectory}|{backupDestination}";
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).Substring(0, 16);
        }
    }

    public sealed class BackupJobState
    {
        public const int CurrentFormatVersion = 2;

        public int FormatVersion { get; set; } = CurrentFormatVersion;

        public string SourceDirectory { get; set; } = string.Empty;

        public string BackupDestination { get; set; } = string.Empty;

        public DateTime BackupDate { get; set; }

        public long MaxSourceBytesPerPart { get; set; }

        public int MaxFilesPerPart { get; set; }

        public string PlanFingerprint { get; set; } = string.Empty;

        public int TotalParts { get; set; }

        public int TotalFiles { get; set; }

        public long SourceBytes { get; set; }

        public bool Published { get; set; }
    }

    public sealed class BackupPartCheckpoint
    {
        public int FormatVersion { get; set; } = BackupJobState.CurrentFormatVersion;

        public int PartNumber { get; set; }

        public string ArchiveFileName { get; set; } = string.Empty;

        public string Fingerprint { get; set; } = string.Empty;

        public int FileCount { get; set; }

        public long SourceBytes { get; set; }

        public long ArchiveBytes { get; set; }
    }
}
