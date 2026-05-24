using DOPipeline.Entities;
using DOPipeline.Storage;
using DOPipeline.Systems;
using DOPipeline.Utilities;
using DifferentialBackup.Components;
using DifferentialBackup.Utilities;
using DOPipeline.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace DifferentialBackup.Systems
{
    public class BackupExecutionSystem : ISystem, IExecutionScopedSystem
    {
        private readonly string _sourceDirectory;
        private readonly string _backupDestination;
        private readonly ConcurrentDictionary<string, string> _fileHashes;
        private readonly HashSet<DateTime> _backupDates;
        private readonly BackupRunState _backupRunState;
        private readonly IPipelineLogger? _logger;
        private readonly object _archiveLock = new();
        private readonly object _backupDatesLock = new();
        private List<BackupFilePlan> _plannedFiles = new();
        private BackupManifest? _manifest;
        private Dictionary<int, string> _completedFiles = new();
        private string? _partialArchivePath;
        private string? _finalArchivePath;
        private long _totalBytesToWrite;
        private long _bytesWritten;
        private int _filesWrittenOrSkipped;
        private int _filesToWriteOrSkip;
        private int _failureCount;
        private Stopwatch _stopwatch = new();
        private DateTime _lastProgressLogUtc = DateTime.MinValue;

        public BackupExecutionSystem(
            string sourceDirectory,
            string backupDestination,
            ConcurrentDictionary<string, string>? fileHashes = null,
            HashSet<DateTime>? backupDates = null,
            BackupRunState? backupRunState = null,
            IPipelineLogger? logger = null)
        {
            _sourceDirectory = sourceDirectory;
            _backupDestination = backupDestination;
            _fileHashes = fileHashes ?? new ConcurrentDictionary<string, string>();
            _backupDates = backupDates ?? new HashSet<DateTime>();
            _backupRunState = backupRunState ?? new BackupRunState(sourceDirectory, backupDestination);
            _logger = logger;
        }

        public Result BeginExecution(IEnumerable<Entity> entities, IComponentStorage storage)
        {
            _plannedFiles = entities
                .Select(entity => CreatePlan(entity, storage))
                .Where(plan => plan != null)
                .Cast<BackupFilePlan>()
                .ToList();

            _failureCount = 0;
            _bytesWritten = 0;
            _filesWrittenOrSkipped = 0;
            _filesToWriteOrSkip = _plannedFiles.Count;
            _totalBytesToWrite = _plannedFiles.Sum(plan => plan.Length);
            _stopwatch = Stopwatch.StartNew();
            _lastProgressLogUtc = DateTime.MinValue;

            if (_plannedFiles.Count == 0)
            {
                return Result.Success();
            }

            try
            {
                Directory.CreateDirectory(_backupDestination);
                var backupDate = _backupRunState.GetOrCreateBackupDate(_plannedFiles[0].BackupDate);
                _manifest = _backupRunState.CreateOrUpdateManifest(
                    backupDate,
                    _plannedFiles.Select(plan => new BackupManifestFile
                    {
                        SourcePath = plan.FilePath,
                        EntryName = plan.EntryName,
                        Hash = plan.Hash,
                        Length = plan.Length
                    }).ToList());

                _plannedFiles = AssignManifestIndexes(_plannedFiles, _manifest);
                _completedFiles = _backupRunState.LoadCompletedFiles();
                _partialArchivePath = _manifest.StagingArchivePath;
                _finalArchivePath = Path.Combine(_backupDestination, backupDate.ToString("yyyyMMddHHmmss") + ".zip");

                EnsureUsablePartialArchive();
                _logger?.Log($"Backup Execution: writing {_plannedFiles.Count} changed file(s), {_totalBytesToWrite / 1024d / 1024d:0.0} MB total.");
                _logger?.Log($"Backup Execution: staging archive at '{_partialArchivePath}'.");

                return Result.Success();
            }
            catch (Exception ex)
            {
                return Result.Fail($"Failed to prepare backup archive: {ex.Message}");
            }
        }

        public Result Execute(Entity entity, IComponentStorage storage)
        {
            var backupDateComponent = storage.GetComponent<BackupDateComponent>(entity);
            var filePathComponent = storage.GetComponent<FilePathComponent>(entity);

            if (backupDateComponent == null)
            {
                // No backup needed for this entity
                return Result.Success();
            }

            if (filePathComponent == null)
                return Result.Fail("FilePathComponent missing.");

            try
            {
                var relativePath = Path.GetRelativePath(_sourceDirectory, filePathComponent.FilePath);
                var entryName = NormalizeZipEntryName(relativePath);
                var currentHash = storage.GetComponent<FileHashComponent>(entity)?.CurrentHash;
                var plan = _plannedFiles.FirstOrDefault(plannedFile =>
                    string.Equals(plannedFile.EntryName, entryName, StringComparison.OrdinalIgnoreCase));

                if (string.IsNullOrEmpty(currentHash))
                {
                    return Result.Fail("FileHashComponent missing.");
                }

                if (plan == null)
                {
                    return Result.Fail("Backup manifest entry missing.");
                }

                lock (_archiveLock)
                {
                    if (IsAlreadyCompleted(plan))
                    {
                        _fileHashes[filePathComponent.FilePath] = currentHash;
                        _bytesWritten += plan.Length;
                        _filesWrittenOrSkipped++;
                        LogBackupProgress(force: false);
                        return Result.Success();
                    }

                    WriteFileToArchive(filePathComponent.FilePath, entryName);
                    _backupRunState.AppendCompletedFile(plan.Index, currentHash);
                    _completedFiles[plan.Index] = currentHash;
                    _fileHashes[filePathComponent.FilePath] = currentHash;
                    _filesWrittenOrSkipped++;
                    LogBackupProgress(force: false);
                }

                return Result.Success();
            }
            catch (Exception ex)
            {
                _failureCount++;
                return Result.Fail($"Failed to store file version: {ex.Message}");
            }
        }

        public Result EndExecution(IEnumerable<Entity> entities, IComponentStorage storage)
        {
            if (_plannedFiles.Count == 0)
            {
                return Result.Success();
            }

            lock (_archiveLock)
            {
                try
                {
                    LogBackupProgress(force: true);

                    if (_failureCount > 0)
                    {
                        return Result.Fail($"Backup archive left resumable after {_failureCount} file failure(s).");
                    }

                    if (_partialArchivePath == null || _finalArchivePath == null || _manifest == null)
                    {
                        return Result.Fail("Backup archive was not initialized.");
                    }

                    CopyStagedArchiveToDestination(_partialArchivePath, _finalArchivePath);
                    lock (_backupDatesLock)
                    {
                        _backupDates.Add(_manifest.BackupDate);
                    }
                    _backupRunState.ClearRun();
                    _logger?.Log($"Backup Execution: finalized archive '{_finalArchivePath}'.");

                    return Result.Success();
                }
                catch (Exception ex)
                {
                    return Result.Fail($"Failed to finalize backup archive: {ex.Message}");
                }
            }
        }

        private BackupFilePlan? CreatePlan(Entity entity, IComponentStorage storage)
        {
            var backupDateComponent = storage.GetComponent<BackupDateComponent>(entity);
            var filePathComponent = storage.GetComponent<FilePathComponent>(entity);
            var fileHashComponent = storage.GetComponent<FileHashComponent>(entity);

            if (backupDateComponent == null || filePathComponent == null || fileHashComponent == null)
            {
                return null;
            }

            return new BackupFilePlan(
                -1,
                filePathComponent.FilePath,
                NormalizeZipEntryName(Path.GetRelativePath(_sourceDirectory, filePathComponent.FilePath)),
                fileHashComponent.CurrentHash,
                backupDateComponent.BackupDate,
                new FileInfo(filePathComponent.FilePath).Length);
        }

        private static List<BackupFilePlan> AssignManifestIndexes(
            IReadOnlyCollection<BackupFilePlan> plans,
            BackupManifest manifest)
        {
            var filesByEntry = manifest.Files.ToDictionary(file => file.EntryName, StringComparer.OrdinalIgnoreCase);

            return plans.Select(plan =>
            {
                var manifestFile = filesByEntry[plan.EntryName];
                return plan with { Index = manifestFile.Index };
            }).ToList();
        }

        private void EnsureUsablePartialArchive()
        {
            if (_partialArchivePath == null || _manifest == null)
            {
                throw new InvalidOperationException("Backup archive was not initialized.");
            }

            try
            {
                using var archive = ZipFile.Open(_partialArchivePath, ZipArchiveMode.Update);
                RemoveMissingCheckpointEntries(archive);
            }
            catch (InvalidDataException)
            {
                File.Delete(_partialArchivePath);
                _completedFiles.Clear();
                _backupRunState.ClearCompletedLog();
                using var archive = ZipFile.Open(_partialArchivePath, ZipArchiveMode.Create);
            }
        }

        private void RemoveMissingCheckpointEntries(ZipArchive archive)
        {
            if (_manifest == null)
            {
                return;
            }

            var manifestEntriesByIndex = _manifest.Files.ToDictionary(file => file.Index);
            var missingIndexes = _completedFiles
                .Where(completed =>
                    !manifestEntriesByIndex.TryGetValue(completed.Key, out var manifestFile) ||
                    completed.Value != manifestFile.Hash ||
                    archive.GetEntry(manifestFile.EntryName) == null)
                .Select(completed => completed.Key)
                .ToList();

            foreach (var index in missingIndexes)
            {
                _completedFiles.Remove(index);
            }
        }

        private bool IsAlreadyCompleted(BackupFilePlan plan)
        {
            if (_partialArchivePath == null)
            {
                return false;
            }

            if (!_completedFiles.TryGetValue(plan.Index, out var completedHash) ||
                completedHash != plan.Hash)
            {
                return false;
            }

            using var archive = ZipFile.OpenRead(_partialArchivePath);
            return archive.GetEntry(plan.EntryName) != null;
        }

        private void WriteFileToArchive(string filePath, string entryName)
        {
            if (_partialArchivePath == null)
            {
                throw new InvalidOperationException("Backup archive was not initialized.");
            }

            using var archive = ZipFile.Open(_partialArchivePath, ZipArchiveMode.Update);
            archive.GetEntry(entryName)?.Delete();
            var entry = archive.CreateEntry(entryName, GetCompressionLevel(filePath));

            using var input = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var output = entry.Open();
            var buffer = new byte[1024 * 1024];
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                output.Write(buffer, 0, read);
                _bytesWritten += read;
                LogBackupProgress(force: false);
            }
        }

        private void LogBackupProgress(bool force)
        {
            if (_logger == null || _filesToWriteOrSkip == 0)
            {
                return;
            }

            var now = DateTime.UtcNow;
            if (!force && now - _lastProgressLogUtc < TimeSpan.FromSeconds(1))
            {
                return;
            }

            _lastProgressLogUtc = now;
            var percent = _totalBytesToWrite == 0
                ? _filesWrittenOrSkipped * 100.0 / _filesToWriteOrSkip
                : Math.Min(100, _bytesWritten * 100.0 / _totalBytesToWrite);
            var eta = CalculateEta(percent);

            ReportProgress(
                $"Backup Execution: {percent:0.0}% complete, " +
                $"{_bytesWritten / 1024d / 1024d:0.0}/{_totalBytesToWrite / 1024d / 1024d:0.0} MB, " +
                $"{_filesWrittenOrSkipped}/{_filesToWriteOrSkip} file(s), ETA: {eta}");
        }

        private void ReportProgress(string message)
        {
            if (_logger is IProgressLogger progressLogger)
            {
                progressLogger.ReportProgress(message);
            }
        }

        private string CalculateEta(double percent)
        {
            if (percent <= 0 || percent >= 100)
            {
                return "00:00:00";
            }

            var remainingTicks = _stopwatch.Elapsed.Ticks * (100 - percent) / percent;
            return TimeSpan.FromTicks((long)remainingTicks).ToString(@"hh\:mm\:ss");
        }

        private static string NormalizeZipEntryName(string relativePath)
        {
            return relativePath
                .Replace(Path.DirectorySeparatorChar, '/')
                .Replace(Path.AltDirectorySeparatorChar, '/');
        }

        private void CopyStagedArchiveToDestination(string stagedArchivePath, string finalArchivePath)
        {
            var copyingPath = finalArchivePath + ".copying";
            if (File.Exists(copyingPath))
            {
                File.Delete(copyingPath);
            }

            _logger?.Log($"Backup Execution: copying staged archive to '{finalArchivePath}'.");
            File.Copy(stagedArchivePath, copyingPath);

            if (File.Exists(finalArchivePath))
            {
                File.Delete(finalArchivePath);
            }

            File.Move(copyingPath, finalArchivePath);
        }

        private static CompressionLevel GetCompressionLevel(string filePath)
        {
            var extension = Path.GetExtension(filePath).ToLowerInvariant();
            return PreCompressedExtensions.Contains(extension)
                ? CompressionLevel.NoCompression
                : CompressionLevel.Optimal;
        }

        private static readonly HashSet<string> PreCompressedExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".7z",
            ".avi",
            ".bz2",
            ".gif",
            ".gz",
            ".heic",
            ".jpeg",
            ".jpg",
            ".m4v",
            ".mkv",
            ".mov",
            ".mp3",
            ".mp4",
            ".pdf",
            ".png",
            ".rar",
            ".webm",
            ".webp",
            ".wmv",
            ".zip"
        };

        private sealed record BackupFilePlan(int Index, string FilePath, string EntryName, string Hash, DateTime BackupDate, long Length);
    }
}
