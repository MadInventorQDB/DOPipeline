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
                var operationEntity = entities.FirstOrDefault(entity => storage.GetComponent<OperationComponent>(entity) != null)
                    ?? storage.Query<OperationComponent>().FirstOrDefault();
                var runId = operationEntity == null
                    ? Guid.Empty
                    : storage.GetComponent<OperationComponent>(operationEntity)?.RunId ?? Guid.Empty;
                var operation = operationEntity == null
                    ? null
                    : storage.GetComponent<OperationComponent>(operationEntity);
                if (operation?.Phase is OperationPhase.Finalizing or
                    OperationPhase.PublishedPendingState or OperationPhase.Finished)
                {
                    return Result.Success();
                }
                if (runId != Guid.Empty)
                {
                    var recoveryResult = RecoverPersistedParts(runId, storage);
                    if (!recoveryResult.IsSuccess)
                    {
                        return recoveryResult;
                    }
                }

                var readResult = ReadChangedFiles(entities, storage, runId, out var files);
                if (!readResult.IsSuccess)
                {
                    return readResult;
                }

                if (files.Count == 0 &&
                    !storage.Query<BackupPartComponent, BackupPartStatusComponent>()
                        .Any(entity =>
                        {
                            var part = storage.GetComponent<BackupPartComponent>(entity);
                            var status = storage.GetComponent<BackupPartStatusComponent>(entity);
                            return part != null && status != null &&
                                (runId == Guid.Empty || part.RunId == runId) &&
                                status.State != BackupPartState.Omitted;
                        }))
                {
                    return Result.Success();
                }

                var existingPartEntities = entities
                    .Concat(storage.Query<BackupPartComponent, BackupPartStatusComponent>()
                        .Where(entity => !entities.Contains(entity)))
                    .Distinct()
                    .Select(entity => new
                    {
                        Part = storage.GetComponent<BackupPartComponent>(entity),
                        Status = storage.GetComponent<BackupPartStatusComponent>(entity)
                    })
                    .Where(item => item.Part != null && item.Status != null)
                    .Select(item => new PartEntity(item.Part!, item.Status!))
                    .Where(item => runId == Guid.Empty ||
                        (item.Part.RunId == runId &&
                         (item.Status.RunId == Guid.Empty || item.Status.RunId == runId)))
                    .Where(item => item.Status.State != BackupPartState.Omitted)
                    .OrderBy(item => item.Part.PartNumber)
                    .ToList();
                var existingFiles = existingPartEntities
                    .SelectMany(item => item.Part.Files)
                    .Select(file => file.SourcePath)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                files = files
                    .Where(file => !existingFiles.Contains(file.File.SourcePath))
                    .ToList();
                if (files.Count == 0 && existingPartEntities.Count == 0)
                {
                    return Result.Success();
                }

                var groupedFiles = CreateGroups(files);
                var highestExistingPart = existingPartEntities.Count == 0
                    ? 0
                    : existingPartEntities.Max(item => item.Part.PartNumber);
                var startPartNumber = Math.Max(
                    highestExistingPart,
                    _runState.HighestReservedPartNumber) + 1;
                var provisionalParts = CreatePartComponents(groupedFiles, DateTime.MinValue, startPartNumber);
                var planFingerprint = BackupFingerprint.ForParts(provisionalParts);
                var preferredBackupDate = files.Count > 0
                    ? files[0].BackupDate
                    : existingPartEntities.FirstOrDefault()?.Part.BackupDate ??
                      _runState.GetOrCreateBackupDate(DateTime.UtcNow);
                var backupDate = ResolveBackupDate(preferredBackupDate, planFingerprint);
                var parts = CreatePartComponents(groupedFiles, backupDate, startPartNumber);
                foreach (var part in parts)
                {
                    part.RunId = runId;
                }
                var allParts = existingPartEntities.Select(item => item.Part).Concat(parts).ToList();
                planFingerprint = BackupFingerprint.ForParts(allParts);
                var timestamp = backupDate.ToString("yyyyMMddHHmmss");
                var workingDirectory = Path.Combine(_backupDestination, timestamp + ".backup.copying");
                var finalDirectory = Path.Combine(_backupDestination, timestamp + ".backup");
                var sourceBytes = allParts.Sum(part => part.SourceBytes);
                var totalFiles = allParts.Sum(part => part.Files.Count);

                _runState.CreateOrUpdateJob(
                    backupDate,
                    _options,
                    planFingerprint,
                    allParts.Count,
                    totalFiles,
                    sourceBytes,
                    runId);

                foreach (var part in parts.OrderBy(item => item.PartNumber))
                {
                    // Reservation and the immutable plan are durable before
                    // execution opens a source stream. A later omitted part
                    // therefore leaves a permanent numbering gap.
                    _runState.ReservePartNumber(part.PartNumber);
                    _runState.SavePartPlan(new BackupPartPlan
                    {
                        RunId = runId,
                        PartNumber = part.PartNumber,
                        BackupDate = backupDate,
                        ArchiveFileName = part.ArchiveFileName,
                        Fingerprint = part.Fingerprint,
                        SourceBytes = part.SourceBytes,
                        Files = part.Files.ToList()
                    });
                }

                Directory.CreateDirectory(_backupDestination);
                RemoveStalePartFiles(allParts, workingDirectory);

                var runEntity = storage.Query<BackupRunComponent>()
                    .FirstOrDefault(candidate => storage.GetComponent<BackupRunComponent>(candidate)?.RunId == runId);
                var run = runEntity == null
                    ? new BackupRunComponent()
                    : storage.GetComponent<BackupRunComponent>(runEntity)!;
                run.BackupDate = backupDate;
                run.RunId = runId;
                run.SourceDirectory = _sourceDirectory;
                run.BackupDestination = _backupDestination;
                run.StagingDirectory = _runState.StagingDirectory;
                run.WorkingDirectory = workingDirectory;
                run.FinalDirectory = finalDirectory;
                run.PlanFingerprint = planFingerprint;
                run.TotalParts = allParts.Count;
                run.TotalFiles = totalFiles;
                run.SourceBytes = sourceBytes;
                if (runEntity == null)
                {
                    runEntity = new Entity();
                }
                storage.SetComponent(runEntity, run);
                var allocationEntity = operationEntity ?? runEntity;
                storage.SetComponent(allocationEntity, new PartAllocationComponent
                {
                    RunId = runId,
                    HighestReservedPartNumber = Math.Max(
                        _runState.HighestReservedPartNumber,
                        allParts.Count == 0 ? 0 : allParts.Max(part => part.PartNumber))
                });

                var fileWorkByPath = new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase);
                foreach (var entity in storage.Query<FileWorkComponent>())
                {
                    var component = storage.GetComponent<FileWorkComponent>(entity);
                    if (component != null && component.RunId == runId && !string.IsNullOrEmpty(component.SourcePath))
                    {
                        fileWorkByPath[component.SourcePath] = entity;
                    }
                }

                foreach (var part in parts)
                {
                    var partEntity = new Entity();
                    storage.SetComponent(partEntity, part);
                    storage.SetComponent(partEntity, new BackupPartStatusComponent
                    {
                        RunId = runId,
                        State = BackupPartState.Planned,
                        LocalArchivePath = Path.Combine(_runState.StagingDirectory, part.ArchiveFileName),
                        DestinationArchivePath = Path.Combine(workingDirectory, part.ArchiveFileName)
                    });
                    foreach (var file in part.Files)
                    {
                        if (fileWorkByPath.TryGetValue(file.SourcePath, out var fileEntity))
                        {
                            var work = storage.GetComponent<FileWorkComponent>(fileEntity)!;
                            work.PartNumber = part.PartNumber;
                            storage.SetComponent(fileEntity, work);
                        }
                    }
                }

                _logger?.Log(
                    $"Backup Planning: {files.Count} changed file(s), " +
                    $"{sourceBytes / 1024d / 1024d:0.0} MB, {parts.Count} part(s).");
                _logger?.Log($"Backup Planning: local staging is '{_runState.StagingDirectory}'.");

                return Result.Success();
            }
            catch (Exception ex)
            {
                return Result.Fail($"Failed to plan backup parts: {ex.Message}", ex);
            }
        }

        private Result RecoverPersistedParts(Guid runId, IComponentStorage storage)
        {
            var job = _runState.Job;
            if (job == null || job.Published)
            {
                return Result.Success();
            }

            var existingNumbers = storage.Query<BackupPartComponent, BackupPartStatusComponent>()
                .Where(entity => storage.GetComponent<BackupPartComponent>(entity)?.RunId == runId)
                .Select(entity => storage.GetComponent<BackupPartComponent>(entity)?.PartNumber)
                .Where(number => number.HasValue)
                .Select(number => number!.Value)
                .ToHashSet();
            var workingDirectory = Path.Combine(
                _backupDestination,
                job.BackupDate.ToString("yyyyMMddHHmmss") + ".backup.copying");

            // TotalParts is a count of active parts, not a maximum part
            // number. Omitted parts leave intentional gaps, so recover every
            // persisted identity discovered in the owned staging/output paths.
            var persistedNumbers = EnumeratePersistedPartNumbers(workingDirectory);
            persistedNumbers.UnionWith(EnumeratePersistedPartNumbers(_runState.StagingDirectory));
            var unacknowledgedArtifacts = new List<string>();

            foreach (var partNumber in persistedNumbers.OrderBy(number => number))
            {
                if (existingNumbers.Contains(partNumber))
                {
                    continue;
                }

                var checkpoint = _runState.LoadPartCheckpoint(partNumber);
                if (checkpoint == null)
                {
                    var plan = _runState.LoadPartPlan(partNumber);
                    if (plan != null)
                    {
                        if (plan.PartNumber != partNumber ||
                            !string.Equals(plan.ArchiveFileName,
                                $"part-{partNumber:000000}.zip",
                                StringComparison.OrdinalIgnoreCase) ||
                            plan.Files == null ||
                            plan.State is not (BackupPartState.Planned or BackupPartState.Omitted) ||
                            plan.SourceBytes != plan.Files.Sum(file => file.Length) ||
                            !string.Equals(plan.Fingerprint,
                                BackupFingerprint.ForFiles(plan.Files),
                                StringComparison.Ordinal) ||
                            (plan.RunId != Guid.Empty && plan.RunId !=
                                (_runState.Job?.RunId ?? plan.RunId)))
                        {
                            return Result.Fail($"Backup part plan {partNumber} is inconsistent.");
                        }

                        unacknowledgedArtifacts.AddRange(
                            EnumerateUnacknowledgedArtifacts(workingDirectory, partNumber));
                        unacknowledgedArtifacts.AddRange(
                            EnumerateUnacknowledgedArtifacts(_runState.StagingDirectory, partNumber));

                        var plannedEntity = new Entity();
                        var plannedPart = new BackupPartComponent
                        {
                            RunId = runId,
                            PartNumber = plan.PartNumber,
                            BackupDate = plan.BackupDate == default ? job.BackupDate : plan.BackupDate,
                            ArchiveFileName = plan.ArchiveFileName,
                            Fingerprint = plan.Fingerprint,
                            SourceBytes = plan.SourceBytes,
                            Files = plan.Files.ToArray()
                        };
                        storage.SetComponent(plannedEntity, plannedPart);
                        var plannedState = plan.State == BackupPartState.Omitted || plannedPart.Files.Count == 0
                            ? BackupPartState.Omitted
                            : BackupPartState.Planned;
                        storage.SetComponent(plannedEntity, new BackupPartStatusComponent
                        {
                            RunId = runId,
                            State = plannedState,
                            LocalArchivePath = Path.Combine(_runState.StagingDirectory, plannedPart.ArchiveFileName),
                            DestinationArchivePath = Path.Combine(workingDirectory, plannedPart.ArchiveFileName)
                        });
                        RestorePlannedFileRows(plannedPart, plannedState, runId, storage);
                        existingNumbers.Add(partNumber);
                        continue;
                    }

                    return Result.Fail($"Unexpected backup part {partNumber} has no durable plan or receipt; artifacts were preserved.");
                }

                if (string.IsNullOrWhiteSpace(checkpoint.ArchiveFileName) ||
                    !string.Equals(Path.GetFileName(checkpoint.ArchiveFileName), checkpoint.ArchiveFileName, StringComparison.Ordinal) ||
                    !string.Equals(
                        checkpoint.ArchiveFileName,
                        $"part-{partNumber:000000}.zip",
                        StringComparison.OrdinalIgnoreCase) ||
                    checkpoint.Files == null ||
                    checkpoint.Files.Count != checkpoint.FileCount ||
                    checkpoint.Fingerprint != BackupFingerprint.ForFiles(checkpoint.Files) ||
                    checkpoint.SourceBytes != checkpoint.Files.Sum(file => file.Length) ||
                    checkpoint.ArchiveBytes <= 0 ||
                    string.IsNullOrWhiteSpace(checkpoint.ArchiveSha256))
                {
                    return Result.Fail($"Backup part checkpoint {partNumber} is inconsistent.");
                }

                var localPath = Path.Combine(_runState.StagingDirectory, checkpoint.ArchiveFileName);
                var destinationPath = Path.Combine(workingDirectory, checkpoint.ArchiveFileName);
                var localValid = IsValidArtifact(localPath, checkpoint.ArchiveBytes, checkpoint.ArchiveSha256);
                var destinationValid = IsValidArtifact(destinationPath, checkpoint.ArchiveBytes, checkpoint.ArchiveSha256);
                if (!localValid && !destinationValid)
                {
                    return Result.Fail($"Acknowledged backup part {partNumber} has no valid staged or transferred copy.");
                }

                var entity = new Entity();
                var part = new BackupPartComponent
                {
                    RunId = runId,
                    PartNumber = checkpoint.PartNumber,
                    BackupDate = job.BackupDate,
                    ArchiveFileName = checkpoint.ArchiveFileName,
                    Fingerprint = checkpoint.Fingerprint,
                    SourceBytes = checkpoint.SourceBytes,
                    Files = checkpoint.Files.ToArray()
                };
                storage.SetComponent(entity, part);
                storage.SetComponent(entity, new BackupPartStatusComponent
                {
                    RunId = runId,
                    State = destinationValid ? BackupPartState.Transferred : BackupPartState.Staged,
                    LocalArchivePath = localPath,
                    DestinationArchivePath = destinationPath,
                    ArchiveBytes = checkpoint.ArchiveBytes
                });
                storage.SetComponent(entity, new PartReceiptComponent
                {
                    RunId = runId,
                    PartNumber = checkpoint.PartNumber,
                    Files = part.Files,
                    Fingerprint = part.Fingerprint,
                    ArchiveLength = checkpoint.ArchiveBytes,
                    ArchiveSha256 = checkpoint.ArchiveSha256,
                    IndexLength = new FileInfo(_runState.GetPartCheckpointPath(partNumber)).Length,
                    IndexSha256 = ComputeFileHash(_runState.GetPartCheckpointPath(partNumber))
                });
                RestoreRecoveredFileRows(part, runId, storage);
                var persistedPlan = _runState.LoadPartPlan(partNumber);
                if (persistedPlan != null)
                {
                    RestoreUnacknowledgedPlannedRows(
                        persistedPlan,
                        checkpoint.Files,
                        part.BackupDate,
                        runId,
                        storage);
                }
                existingNumbers.Add(partNumber);
            }

            foreach (var artifact in unacknowledgedArtifacts.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                File.Delete(artifact);
            }

            return Result.Success();
        }

        private static IEnumerable<string> EnumerateUnacknowledgedArtifacts(
            string directory,
            int partNumber)
        {
            if (!Directory.Exists(directory))
            {
                yield break;
            }

            var prefix = $"part-{partNumber:000000}";
            foreach (var path in Directory.EnumerateFiles(directory, prefix + "*"))
            {
                var name = Path.GetFileName(path);
                if (string.Equals(name, prefix + ".zip", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, prefix + ".zip.partial", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, prefix + ".zip.copying", StringComparison.OrdinalIgnoreCase))
                {
                    yield return path;
                }
                else if (string.Equals(name, prefix + ".plan.json", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(name, prefix + ".index.json", StringComparison.OrdinalIgnoreCase) ||
                         name.StartsWith(prefix + ".plan.json.", StringComparison.OrdinalIgnoreCase) ||
                         name.StartsWith(prefix + ".index.json.", StringComparison.OrdinalIgnoreCase))
                {
                    // Durable descriptors are not temporary capture output.
                }
                else
                {
                    throw new InvalidDataException(
                        $"Unexpected backup part artifact for part {partNumber}: '{path}'.");
                }
            }
        }

        private static void RestorePlannedFileRows(
            BackupPartComponent part,
            BackupPartState partState,
            Guid runId,
            IComponentStorage storage)
        {
            var fileWorkByKey = new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase);
            foreach (var candidate in storage.Query<FileWorkComponent>())
            {
                var component = storage.GetComponent<FileWorkComponent>(candidate);
                if (component != null && component.RunId == runId && !string.IsNullOrEmpty(component.StableKey))
                {
                    fileWorkByKey[component.StableKey] = candidate;
                }
            }

            foreach (var descriptor in part.Files)
            {
                if (!fileWorkByKey.TryGetValue(descriptor.SourcePath, out var entity))
                {
                    entity = new Entity();
                    fileWorkByKey[descriptor.SourcePath] = entity;
                }

                storage.SetComponent(entity, new FilePathComponent { FilePath = descriptor.SourcePath });
                var work = storage.GetComponent<FileWorkComponent>(entity) ?? new FileWorkComponent
                {
                    RunId = runId,
                    StableKey = descriptor.SourcePath,
                    SourcePath = descriptor.SourcePath,
                    RelativePath = descriptor.EntryName,
                    RequestedPass = 0,
                    LastAttemptedPass = -1
                };
                work.RunId = runId;
                work.StableKey = descriptor.SourcePath;
                work.SourcePath = descriptor.SourcePath;
                work.RelativePath = descriptor.EntryName;
                work.PartNumber = part.PartNumber;
                if (work.State is not (FileWorkState.Captured or FileWorkState.Unchanged))
                {
                    work.State = partState == BackupPartState.Omitted
                        ? FileWorkState.Deferred
                        : FileWorkState.ReadyToCapture;
                }
                storage.SetComponent(entity, work);
                storage.SetComponent(entity, new BackupDateComponent { BackupDate = part.BackupDate });

                if (partState == BackupPartState.Omitted &&
                    storage.GetComponent<BackupIssueComponent>(entity) == null)
                {
                    var now = DateTimeOffset.UtcNow;
                    storage.SetComponent(entity, new BackupIssueComponent
                    {
                        RunId = runId,
                        StableKey = descriptor.SourcePath,
                        Path = descriptor.SourcePath,
                        Stage = "Recovery",
                        Category = "SourceDeferred",
                        OriginalMessage = "The planned source path was unavailable before the previous run stopped.",
                        AttemptCount = 1,
                        FirstOccurrenceUtc = now,
                        LastOccurrenceUtc = now,
                        Retryable = true,
                        Resolved = false
                    });
                }
            }
        }

        private static bool IsValidArtifact(string path, long expectedLength, string expectedHash)
        {
            if (!File.Exists(path) || new FileInfo(path).Length != expectedLength)
            {
                return false;
            }

            return string.IsNullOrWhiteSpace(expectedHash) ||
                string.Equals(ComputeFileHash(path), expectedHash, StringComparison.OrdinalIgnoreCase);
        }

        private HashSet<int> EnumeratePersistedPartNumbers(string directory)
        {
            var result = new HashSet<int>();
            if (!Directory.Exists(directory))
            {
                return result;
            }

            foreach (var path in Directory.EnumerateFiles(directory, "part-*") )
            {
                var name = Path.GetFileName(path);
                var marker = name.IndexOf("part-", StringComparison.OrdinalIgnoreCase);
                if (marker < 0 || name.Length < marker + 11)
                {
                    continue;
                }

                var digits = name.Substring(marker + 5, 6);
                if (digits.All(char.IsDigit) && int.TryParse(digits, out var number) && number > 0)
                {
                    result.Add(number);
                }
            }

            return result;
        }

        private static void RestoreRecoveredFileRows(
            BackupPartComponent part,
            Guid runId,
            IComponentStorage storage)
        {
            var fileWorkByKey = new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase);
            foreach (var candidate in storage.Query<FileWorkComponent>())
            {
                var component = storage.GetComponent<FileWorkComponent>(candidate);
                if (component != null && component.RunId == runId && !string.IsNullOrEmpty(component.StableKey))
                {
                    fileWorkByKey[component.StableKey] = candidate;
                }
            }

            foreach (var descriptor in part.Files)
            {
                if (!fileWorkByKey.TryGetValue(descriptor.SourcePath, out var entity))
                {
                    entity = new Entity();
                    fileWorkByKey[descriptor.SourcePath] = entity;
                }

                storage.SetComponent(entity, new FilePathComponent { FilePath = descriptor.SourcePath });
                var existingWork = storage.GetComponent<FileWorkComponent>(entity) ?? new FileWorkComponent
                {
                    RunId = runId,
                    StableKey = descriptor.SourcePath,
                    SourcePath = descriptor.SourcePath,
                    RelativePath = descriptor.EntryName
                };
                existingWork.RunId = runId;
                existingWork.StableKey = descriptor.SourcePath;
                existingWork.SourcePath = descriptor.SourcePath;
                existingWork.RelativePath = descriptor.EntryName;
                existingWork.PartNumber = part.PartNumber;
                existingWork.State = FileWorkState.Captured;
                storage.SetComponent(entity, existingWork);
                storage.SetComponent(entity, new FileHashComponent
                {
                    PreviousHash = descriptor.Hash,
                    CurrentHash = descriptor.Hash,
                    CurrentLength = descriptor.Length
                });
                storage.SetComponent(entity, new BackupDateComponent { BackupDate = part.BackupDate });
            }
        }

        private static void RestoreUnacknowledgedPlannedRows(
            BackupPartPlan plan,
            IReadOnlyList<BackupPartFile> acknowledgedFiles,
            DateTime backupDate,
            Guid runId,
            IComponentStorage storage)
        {
            var acknowledged = acknowledgedFiles
                .Select(file => file.SourcePath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var fileWorkByKey = new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase);
            foreach (var candidate in storage.Query<FileWorkComponent>())
            {
                var component = storage.GetComponent<FileWorkComponent>(candidate);
                if (component != null && component.RunId == runId && !string.IsNullOrEmpty(component.StableKey))
                {
                    fileWorkByKey[component.StableKey] = candidate;
                }
            }

            foreach (var descriptor in plan.Files)
            {
                if (acknowledged.Contains(descriptor.SourcePath))
                {
                    continue;
                }

                if (!fileWorkByKey.TryGetValue(descriptor.SourcePath, out var entity))
                {
                    entity = new Entity();
                    fileWorkByKey[descriptor.SourcePath] = entity;
                }

                storage.SetComponent(entity, new FilePathComponent { FilePath = descriptor.SourcePath });
                var work = storage.GetComponent<FileWorkComponent>(entity) ?? new FileWorkComponent
                {
                    RunId = runId,
                    StableKey = descriptor.SourcePath,
                    SourcePath = descriptor.SourcePath,
                    RelativePath = descriptor.EntryName,
                    LastAttemptedPass = -1
                };
                work.RunId = runId;
                work.StableKey = descriptor.SourcePath;
                work.SourcePath = descriptor.SourcePath;
                work.RelativePath = descriptor.EntryName;
                work.PartNumber = null;
                if (work.State is not (FileWorkState.Captured or FileWorkState.Unchanged))
                {
                    work.State = FileWorkState.Deferred;
                }
                storage.SetComponent(entity, work);
                storage.SetComponent(entity, new BackupDateComponent { BackupDate = backupDate });

                if (storage.GetComponent<BackupIssueComponent>(entity) == null)
                {
                    var now = DateTimeOffset.UtcNow;
                    storage.SetComponent(entity, new BackupIssueComponent
                    {
                        RunId = runId,
                        StableKey = descriptor.SourcePath,
                        Path = descriptor.SourcePath,
                        Stage = "Recovery",
                        Category = "SourceDeferred",
                        OriginalMessage = "The planned source path was not present in the acknowledged part receipt.",
                        AttemptCount = 1,
                        FirstOccurrenceUtc = now,
                        LastOccurrenceUtc = now,
                        Retryable = true,
                        Resolved = false
                    });
                }
            }
        }

        private static string ComputeFileHash(string path)
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
        }

        public Result Execute(Entity entity, IComponentStorage storage)
        {
            return Result.Fail("Backup part planning requires entity-set execution.");
        }

        public Result RecoverPersistedPartsForInitialization(
            Guid runId,
            IComponentStorage storage)
        {
            try
            {
                return RecoverPersistedParts(runId, storage);
            }
            catch (Exception ex)
            {
                return Result.Fail($"Failed to recover persisted backup parts: {ex.Message}", ex);
            }
        }

        private Result ReadChangedFiles(
            IReadOnlyList<Entity> entities,
            IComponentStorage storage,
            Guid runId,
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

                var work = storage.GetComponent<FileWorkComponent>(entity);
                if (work != null && runId != Guid.Empty && work.RunId != runId)
                {
                    continue;
                }

                var error = storage.GetComponent<ErrorComponent>(entity);
                if (error != null)
                {
                    return Result.Fail(
                        $"Cannot create a complete backup because '{path.FilePath}' failed: {error.ErrorMessage}");
                }
                var issue = storage.GetComponent<BackupIssueComponent>(entity);
                if (work?.State is FileWorkState.Deferred or FileWorkState.Omitted ||
                    issue is { Resolved: false })
                {
                    continue;
                }

                if (work != null && work.State is not FileWorkState.ReadyToCapture and not FileWorkState.Discovered)
                {
                    continue;
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
                    if (work != null)
                    {
                        if (work.State != FileWorkState.Omitted)
                        {
                            work.State = FileWorkState.Deferred;
                            storage.SetComponent(entity, work);
                        }

                        continue;
                    }

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
            DateTime backupDate,
            int startPartNumber)
        {
            return groups.Select((files, index) => new BackupPartComponent
            {
                PartNumber = startPartNumber + index,
                BackupDate = backupDate,
                ArchiveFileName = $"part-{startPartNumber + index:000000}.zip",
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
                    if (name.EndsWith(".partial", StringComparison.OrdinalIgnoreCase) &&
                        expectedArchiveNames.Contains(archiveName))
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
                    if (name.EndsWith(".copying", StringComparison.OrdinalIgnoreCase) &&
                        expectedArchiveNames.Contains(archiveName))
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

        private sealed record PartEntity(
            BackupPartComponent Part,
            BackupPartStatusComponent Status);
    }
}
