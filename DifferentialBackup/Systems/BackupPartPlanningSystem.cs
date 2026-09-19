using DOPipeline.Components;
using DOPipeline.Entities;
using DOPipeline.Logging;
using DOPipeline.Storage;
using DOPipeline.Systems;
using DOPipeline.Utilities;
using DifferentialBackup.Components;
using DifferentialBackup.Utilities;

namespace DifferentialBackup.Systems
{
    public sealed class BackupPartPlanningSystem : IEntitySetSystem
    {
        private readonly string _sourceDirectory;
        private readonly string _backupDestination;
        private readonly BackupRunState _runState;
        private readonly BackupBatchOptions _options;
        private readonly IPipelineLogger? _logger;

        public BackupPartPlanningSystem(
            string sourceDirectory,
            string backupDestination,
            BackupRunState runState,
            BackupBatchOptions options,
            IPipelineLogger? logger = null)
        {
            _sourceDirectory = Path.GetFullPath(sourceDirectory);
            _backupDestination = Path.GetFullPath(backupDestination);
            _runState = runState;
            _options = options;
            _logger = logger;
            _options.Validate();
        }

        public Result Execute(IReadOnlyList<Entity> entities, IComponentStorage storage)
        {
            try
            {
                var readResult = ReadChangedFiles(entities, storage, out var files);
                if (!readResult.IsSuccess)
                {
                    return readResult;
                }

                if (files.Count == 0)
                {
                    return Result.Success();
                }

                var groupedFiles = CreateGroups(files);
                var provisionalParts = CreatePartComponents(groupedFiles, DateTime.MinValue);
                var planFingerprint = BackupFingerprint.ForParts(provisionalParts);
                var backupDate = ResolveBackupDate(files[0].BackupDate, planFingerprint);
                var parts = CreatePartComponents(groupedFiles, backupDate);
                var timestamp = backupDate.ToString("yyyyMMddHHmmss");
                var workingDirectory = Path.Combine(_backupDestination, timestamp + ".backup.copying");
                var finalDirectory = Path.Combine(_backupDestination, timestamp + ".backup");
                var sourceBytes = parts.Sum(part => part.SourceBytes);

                _runState.CreateOrUpdateJob(
                    backupDate,
                    _options,
                    planFingerprint,
                    parts.Count,
                    files.Count,
                    sourceBytes);

                Directory.CreateDirectory(_backupDestination);
                RemoveStalePartFiles(parts, workingDirectory);

                var runEntity = new Entity();
                storage.SetComponent(runEntity, new BackupRunComponent
                {
                    BackupDate = backupDate,
                    SourceDirectory = _sourceDirectory,
                    BackupDestination = _backupDestination,
                    StagingDirectory = _runState.StagingDirectory,
                    WorkingDirectory = workingDirectory,
                    FinalDirectory = finalDirectory,
                    PlanFingerprint = planFingerprint,
                    TotalParts = parts.Count,
                    TotalFiles = files.Count,
                    SourceBytes = sourceBytes
                });

                foreach (var part in parts)
                {
                    var partEntity = new Entity();
                    storage.SetComponent(partEntity, part);
                    storage.SetComponent(partEntity, new BackupPartStatusComponent
                    {
                        State = BackupPartState.Planned,
                        LocalArchivePath = Path.Combine(_runState.StagingDirectory, part.ArchiveFileName),
                        DestinationArchivePath = Path.Combine(workingDirectory, part.ArchiveFileName)
                    });
                }

                _logger?.Log(
                    $"Backup Planning: {files.Count} changed file(s), " +
                    $"{sourceBytes / 1024d / 1024d:0.0} MB, {parts.Count} part(s).");
                _logger?.Log($"Backup Planning: local staging is '{_runState.StagingDirectory}'.");

                return Result.Success();
            }
            catch (Exception ex)
            {
                return Result.Fail($"Failed to plan backup parts: {ex.Message}");
            }
        }

        public Result Execute(Entity entity, IComponentStorage storage)
        {
            return Result.Fail("Backup part planning requires entity-set execution.");
        }

        private Result ReadChangedFiles(
            IReadOnlyList<Entity> entities,
            IComponentStorage storage,
            out List<ChangedFile> files)
        {
            files = new List<ChangedFile>();
            foreach (var entity in entities)
            {
                var path = storage.GetComponent<FilePathComponent>(entity);
                if (path == null)
                {
                    continue;
                }

                var error = storage.GetComponent<ErrorComponent>(entity);
                if (error != null)
                {
                    return Result.Fail(
                        $"Cannot create a complete backup because '{path.FilePath}' failed: {error.ErrorMessage}");
                }

                var backupDate = storage.GetComponent<BackupDateComponent>(entity);
                if (backupDate == null)
                {
                    continue;
                }

                var hash = storage.GetComponent<FileHashComponent>(entity);
                if (hash == null || string.IsNullOrWhiteSpace(hash.CurrentHash))
                {
                    return Result.Fail($"File hash missing for '{path.FilePath}'.");
                }

                var info = new FileInfo(path.FilePath);
                if (!info.Exists)
                {
                    return Result.Fail($"Source file no longer exists: '{path.FilePath}'.");
                }

                var relativePath = Path.GetRelativePath(_sourceDirectory, info.FullName);
                if (relativePath == ".." || relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                {
                    return Result.Fail($"Source file is outside the backup root: '{path.FilePath}'.");
                }

                files.Add(new ChangedFile(
                    new BackupPartFile(
                        info.FullName,
                        NormalizeZipEntryName(relativePath),
                        hash.CurrentHash,
                        info.Length),
                    backupDate.BackupDate));
            }

            files.Sort((left, right) =>
            {
                var comparison = StringComparer.OrdinalIgnoreCase.Compare(left.File.EntryName, right.File.EntryName);
                return comparison != 0
                    ? comparison
                    : StringComparer.Ordinal.Compare(left.File.EntryName, right.File.EntryName);
            });

            return Result.Success();
        }

        private List<IReadOnlyList<BackupPartFile>> CreateGroups(IReadOnlyList<ChangedFile> files)
        {
            var groups = new List<IReadOnlyList<BackupPartFile>>();
            var current = new List<BackupPartFile>();
            var currentBytes = 0L;

            foreach (var changedFile in files)
            {
                var file = changedFile.File;
                var wouldExceedBytes = current.Count > 0 &&
                    currentBytes > _options.MaxSourceBytesPerPart - Math.Min(file.Length, _options.MaxSourceBytesPerPart);
                if (current.Count >= _options.MaxFilesPerPart || wouldExceedBytes)
                {
                    groups.Add(current.ToArray());
                    current = new List<BackupPartFile>();
                    currentBytes = 0;
                }

                current.Add(file);
                currentBytes += file.Length;

                if (current.Count >= _options.MaxFilesPerPart || currentBytes >= _options.MaxSourceBytesPerPart)
                {
                    groups.Add(current.ToArray());
                    current = new List<BackupPartFile>();
                    currentBytes = 0;
                }
            }

            if (current.Count > 0)
            {
                groups.Add(current.ToArray());
            }

            return groups;
        }

        private static List<BackupPartComponent> CreatePartComponents(
            IReadOnlyList<IReadOnlyList<BackupPartFile>> groups,
            DateTime backupDate)
        {
            return groups.Select((files, index) => new BackupPartComponent
            {
                PartNumber = index + 1,
                BackupDate = backupDate,
                ArchiveFileName = $"part-{index + 1:000000}.zip",
                Fingerprint = BackupFingerprint.ForFiles(files),
                SourceBytes = files.Sum(file => file.Length),
                Files = files
            }).ToList();
        }

        private DateTime ResolveBackupDate(DateTime preferredDate, string planFingerprint)
        {
            var existingJob = _runState.Job;
            if (existingJob is { Published: true } &&
                !string.Equals(existingJob.PlanFingerprint, planFingerprint, StringComparison.Ordinal))
            {
                _runState.ResetRun();
                return FindAvailableBackupDate(DateTime.UtcNow);
            }

            if (existingJob != null)
            {
                return existingJob.BackupDate;
            }

            var candidate = _runState.GetOrCreateBackupDate(preferredDate);
            var timestamp = candidate.ToString("yyyyMMddHHmmss");
            if (!Directory.Exists(Path.Combine(_backupDestination, timestamp + ".backup")) &&
                !Directory.Exists(Path.Combine(_backupDestination, timestamp + ".backup.copying")))
            {
                return candidate;
            }

            _runState.ResetRun();
            return FindAvailableBackupDate(DateTime.UtcNow);
        }

        private DateTime FindAvailableBackupDate(DateTime start)
        {
            var candidate = new DateTime(
                start.Ticks - start.Ticks % TimeSpan.TicksPerSecond,
                start.Kind);

            while (true)
            {
                var timestamp = candidate.ToString("yyyyMMddHHmmss");
                if (!Directory.Exists(Path.Combine(_backupDestination, timestamp + ".backup")) &&
                    !Directory.Exists(Path.Combine(_backupDestination, timestamp + ".backup.copying")))
                {
                    return _runState.GetOrCreateBackupDate(candidate);
                }

                candidate = candidate.AddSeconds(1);
            }
        }

        private void RemoveStalePartFiles(
            IReadOnlyCollection<BackupPartComponent> parts,
            string workingDirectory)
        {
            var expectedArchiveNames = parts
                .Select(part => part.ArchiveFileName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var expectedCheckpointNames = parts
                .Select(part => Path.GetFileName(_runState.GetPartCheckpointPath(part.PartNumber)))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (Directory.Exists(_runState.StagingDirectory))
            {
                foreach (var path in Directory.EnumerateFiles(_runState.StagingDirectory, "part-*"))
                {
                    var name = Path.GetFileName(path);
                    var archiveName = name.EndsWith(".partial", StringComparison.OrdinalIgnoreCase)
                        ? name[..^".partial".Length]
                        : name;
                    if (!expectedArchiveNames.Contains(archiveName) && !expectedCheckpointNames.Contains(name))
                    {
                        File.Delete(path);
                    }
                }
            }

            if (Directory.Exists(workingDirectory))
            {
                foreach (var path in Directory.EnumerateFiles(workingDirectory, "part-*"))
                {
                    var name = Path.GetFileName(path);
                    var archiveName = name.EndsWith(".copying", StringComparison.OrdinalIgnoreCase)
                        ? name[..^".copying".Length]
                        : name;
                    if (!expectedArchiveNames.Contains(archiveName))
                    {
                        File.Delete(path);
                    }
                }
            }
        }

        private static string NormalizeZipEntryName(string relativePath)
        {
            return relativePath
                .Replace(Path.DirectorySeparatorChar, '/')
                .Replace(Path.AltDirectorySeparatorChar, '/');
        }

        private sealed record ChangedFile(BackupPartFile File, DateTime BackupDate);
    }
}
