using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IO.Compression;
using System.Collections.Concurrent;
using DifferentialBackup.Components;

namespace DifferentialBackup.Utilities
{
    public sealed class BackupRunState
    {
        private readonly object _lock = new();
        private readonly string _sourceDirectory;
        private readonly string _backupDestination;
        private readonly string _stagingRoot;
        private readonly string _stagingDirectory;
        private readonly ICheckpointObserver _checkpointObserver;
        private long _journalSequence;

        public BackupRunState(
            string sourceDirectory,
            string backupDestination,
            string? stagingRoot = null,
            ICheckpointObserver? checkpointObserver = null)
        {
            _sourceDirectory = NormalizeDirectory(sourceDirectory);
            _backupDestination = NormalizeDirectory(backupDestination);
            _stagingRoot = NormalizeDirectory(stagingRoot ?? GetDefaultStagingRoot());
            _stagingDirectory = Path.Combine(
                _stagingRoot,
                CreateStableDirectoryName(_sourceDirectory, _backupDestination));
            _checkpointObserver = checkpointObserver ?? NoOpCheckpointObserver.Instance;
        }

        public BackupJobState? Job { get; private set; }

        public Guid RunId => Job?.RunId ?? Guid.Empty;

        public DateTime? BackupDate { get; private set; }

        /// <summary>
        /// The validated manifest used for startup recovery, when an
        /// unfinished published job was found.  It is a snapshot of durable
        /// artifact data, not a second owner of the operation workflow.
        /// </summary>
        public BackupManifest? RecoveredManifest { get; private set; }

        public string StagingDirectory => _stagingDirectory;

        public string JobPath => Path.Combine(_stagingDirectory, "job.json");

        public string FinalManifestStagingPath => Path.Combine(_stagingDirectory, "manifest.json");

        public string PublicationReceiptPath => Path.Combine(_stagingDirectory, "publication.receipt.json");

        public string JournalPath => Path.Combine(_stagingDirectory, "components.journal");

        public int HighestReservedPartNumber => Job?.HighestReservedPartNumber ?? 0;

        public string GetPartPlanPath(int partNumber)
        {
            return Path.Combine(_stagingDirectory, $"part-{partNumber:000000}.plan.json");
        }

        public void BeginRun()
        {
            lock (_lock)
            {
                Job = null;
                BackupDate = null;
                RecoveredManifest = null;

                if (!Directory.Exists(_stagingDirectory))
                {
                    return;
                }

                Job = LoadJob();
                var journal = LoadAndValidateJournal();
                ReplayJournal(journal);
                if (Job == null)
                {
                    throw new InvalidDataException(
                        $"Staging directory does not contain a compatible backup job: '{_stagingDirectory}'. " +
                        "Remove or relocate it before retrying.");
                }

                BackupDate = Job.BackupDate;
            }
        }

        /// <summary>
        /// Validates a published set when the process stopped after
        /// publication but before the two application state files were
        /// committed. The published artifacts are authoritative; current
        /// source contents are never read. The operation recovery system calls
        /// this with applyState=false and lets BackupStateCommitSystem own the
        /// state transition; the default remains for legacy direct callers.
        /// </summary>
        public bool RecoverPublishedState(
            ConcurrentDictionary<string, string> fileHashes,
            HashSet<DateTime> backupDates,
            bool applyState = true,
            bool completePublication = true)
        {
            lock (_lock)
            {
                if (Job == null)
                {
                    return false;
                }

                var finalDirectory = Path.Combine(
                    _backupDestination,
                    Job.BackupDate.ToString("yyyyMMddHHmmss") + ".backup");
                var workingDirectory = finalDirectory + ".copying";
                var receipt = LoadPublicationReceipt();
                if (receipt != null)
                {
                    if (receipt.FormatVersion != BackupJobState.CurrentFormatVersion ||
                        (receipt.RunId != Guid.Empty && Job.RunId != Guid.Empty &&
                         receipt.RunId != Job.RunId) ||
                        receipt.BackupDate != Job.BackupDate ||
                        !PathsEqual(receipt.FinalDirectory, finalDirectory) ||
                        !PathsEqual(receipt.WorkingDirectory, workingDirectory))
                    {
                        throw new InvalidDataException(
                            $"Publication receipt does not match the unfinished backup job: '{PublicationReceiptPath}'.");
                    }
                }

                var finalExists = Directory.Exists(finalDirectory);
                var workingExists = Directory.Exists(workingDirectory);
                var publishedDirectory = finalExists
                    ? finalDirectory
                    : receipt != null && workingExists
                        ? workingDirectory
                        : finalDirectory;
                var manifestPath = Path.Combine(publishedDirectory, "manifest.json");
                if (!File.Exists(manifestPath))
                {
                    if (!Job.Published && receipt == null)
                    {
                        return false;
                    }

                    throw new InvalidDataException($"Published backup manifest is missing: '{manifestPath}'.");
                }

                BackupManifest manifest;
                try
                {
                    manifest = JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(manifestPath))
                        ?? throw new InvalidDataException("Published backup manifest is empty.");
                }

                catch (JsonException ex)
                {
                    throw new InvalidDataException(
                        $"Published backup manifest is malformed: '{manifestPath}'.", ex);
                }

                RecoveredManifest = manifest;

                if (manifest.FormatVersion is not (2 or 3) ||
                    manifest.BackupDate != Job.BackupDate ||
                    !PathsEqual(manifest.SourceDirectory, _sourceDirectory) ||
                    manifest.Parts == null ||
                    manifest.Parts.Select(part => part.PartNumber).Distinct().Count() != manifest.Parts.Count ||
                    manifest.FileCount != manifest.Parts.Sum(part => part.FileCount) ||
                    manifest.SourceBytes != manifest.Parts.Sum(part => part.SourceBytes) ||
                    !string.Equals(manifest.PlanFingerprint, Job.PlanFingerprint, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Published backup manifest does not match the unfinished job: '{manifestPath}'.");
                }

                if (receipt != null)
                {
                    if (string.IsNullOrWhiteSpace(receipt.ManifestSha256) ||
                        !string.Equals(receipt.ManifestSha256, ComputeFileHash(manifestPath), StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException(
                            $"Publication receipt does not match the published backup: '{PublicationReceiptPath}'.");
                    }

                    if (receipt.Parts == null || receipt.Parts.Count != manifest.Parts.Count)
                    {
                        throw new InvalidDataException(
                            $"Publication receipt part inventory does not match the published backup: '{PublicationReceiptPath}'.");
                    }

                    var receiptParts = receipt.Parts.ToDictionary(part => part.PartNumber);
                    foreach (var manifestPart in manifest.Parts)
                    {
                        if (!receiptParts.TryGetValue(manifestPart.PartNumber, out var receiptPart) ||
                            !string.Equals(receiptPart.ArchiveFileName, manifestPart.ArchiveFileName, StringComparison.Ordinal) ||
                            !string.Equals(receiptPart.Fingerprint, manifestPart.Fingerprint, StringComparison.Ordinal) ||
                            receiptPart.FileCount != manifestPart.FileCount ||
                            receiptPart.SourceBytes != manifestPart.SourceBytes ||
                            receiptPart.ArchiveBytes != manifestPart.ArchiveBytes ||
                            receiptPart.IndexFileName != manifestPart.IndexFileName ||
                            receiptPart.IndexBytes != manifestPart.IndexBytes ||
                            receiptPart.IndexSha256 != manifestPart.IndexSha256 ||
                            (!string.IsNullOrWhiteSpace(manifestPart.ArchiveSha256) &&
                             !string.Equals(receiptPart.ArchiveSha256, manifestPart.ArchiveSha256, StringComparison.OrdinalIgnoreCase)))
                        {
                            throw new InvalidDataException(
                                $"Publication receipt part inventory does not match part {manifestPart.PartNumber}: '{PublicationReceiptPath}'.");
                        }
                    }
                }

                var publishedEntryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var manifestPart in manifest.Parts)
                {
                    var publishedIndex = BackupIndexReader.Read(publishedDirectory, manifestPart);
                    var archiveName = manifestPart.ArchiveFileName;
                    if (!string.Equals(Path.GetFileName(archiveName), archiveName, StringComparison.Ordinal))
                    {
                        throw new InvalidDataException(
                            $"Published backup manifest contains an unsafe part name: '{archiveName}'.");
                    }

                    var archivePath = Path.Combine(publishedDirectory, archiveName);
                    if (!File.Exists(archivePath) ||
                        new FileInfo(archivePath).Length != manifestPart.ArchiveBytes)
                    {
                        throw new InvalidDataException(
                            $"Published backup part is missing or incomplete: '{archivePath}'.");
                    }

                    if (!string.IsNullOrWhiteSpace(manifestPart.ArchiveSha256) &&
                        !string.Equals(ComputeFileHash(archivePath), manifestPart.ArchiveSha256, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException(
                            $"Published backup part checksum is invalid: '{archivePath}'.");
                    }

                    if (manifest.FormatVersion is 2 or 3)
                    {
                        using var archive = ZipFile.OpenRead(archivePath);
                        if (archive.Entries.Count != manifestPart.FileCount)
                        {
                            throw new InvalidDataException(
                                $"Published backup part entry count is invalid: '{archivePath}'.");
                        }

                        foreach (var entry in archive.Entries)
                        {
                            if (!publishedEntryNames.Add(entry.FullName))
                            {
                                throw new InvalidDataException(
                                    $"Published backup contains duplicate entry '{entry.FullName}'.");
                            }
                        }
                    }

                    var checkpoint = publishedIndex ?? LoadPartCheckpoint(manifestPart.PartNumber);
                    if (checkpoint != null && !string.IsNullOrEmpty(checkpoint.ArchiveSha256) &&
                        !string.Equals(checkpoint.ArchiveSha256, ComputeFileHash(archivePath), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException($"Published backup does not match its acknowledged checkpoint: '{archivePath}'.");
                    if (manifest.FormatVersion == BackupManifest.CurrentFormatVersion &&
                        (checkpoint == null ||
                        checkpoint.Files == null ||
                        checkpoint.Files.Count != checkpoint.FileCount ||
                        !string.Equals(checkpoint.Fingerprint, BackupFingerprint.ForFiles(checkpoint.Files), StringComparison.Ordinal) ||
                        !string.Equals(checkpoint.Fingerprint, manifestPart.Fingerprint, StringComparison.Ordinal) ||
                        checkpoint.FileCount != manifestPart.FileCount ||
                        (!string.IsNullOrWhiteSpace(manifestPart.ArchiveSha256) &&
                         !string.IsNullOrWhiteSpace(checkpoint.ArchiveSha256) &&
                         !string.Equals(checkpoint.ArchiveSha256, manifestPart.ArchiveSha256, StringComparison.OrdinalIgnoreCase))))
                    {
                        throw new InvalidDataException(
                            $"Published backup part descriptor is inconsistent: part {manifestPart.PartNumber}.");
                    }

                    if (checkpoint == null || checkpoint.Files == null)
                    {
                        // A v2 set may predate file descriptors in staging. Its
                        // date remains recoverable, while missing hashes cause a
                        // safe recapture on the next differential pass.
                        continue;
                    }

                    // Hash/date application is owned by
                    // BackupStateCommitSystem. This method only validates the
                    // published bytes and leaves the caller's state untouched.
                }

                if (completePublication &&
                    string.Equals(publishedDirectory, workingDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    if (Directory.Exists(finalDirectory))
                    {
                        throw new InvalidDataException(
                            $"Both working and final backup directories exist: '{finalDirectory}'.");
                    }

                    Directory.Move(workingDirectory, finalDirectory);
                    _checkpointObserver.Reached("publication-directory-renamed");
                }

                if (completePublication && !Job.Published)
                {
                    // The directory rename is the publication boundary. If a
                    // process stopped before the job marker was flushed, the
                    // validated final directory is still authoritative.
                    Job.Published = true;
                    Job.StateCommitStep = StateCommitStep.Pending;
                    SaveJsonAtomic(JobPath, Job);
                    _checkpointObserver.Reached("published-job-committed");
                }

                if (applyState)
                {
                    foreach (var manifestPart in manifest.Parts)
                    {
                        var checkpoint = LoadPartCheckpoint(manifestPart.PartNumber);
                        if (checkpoint?.Files == null)
                        {
                            continue;
                        }

                        foreach (var file in checkpoint.Files)
                        {
                            fileHashes[file.SourcePath] = file.Hash;
                        }
                    }

                    backupDates.Add(Job.BackupDate);
                }

                return true;
            }
        }

        /// <summary>
        /// Completes a publication that was already validated by
        /// <see cref="RecoverPublishedState"/> with completePublication=false.
        /// The recovery system decides when this transition is appropriate;
        /// this method only performs the validated filesystem/state writes.
        /// </summary>
        public void CompleteRecoveredPublication()
        {
            lock (_lock)
            {
                if (Job == null)
                {
                    throw new InvalidOperationException("Backup job was not initialized.");
                }

                var finalDirectory = Path.Combine(
                    _backupDestination,
                    Job.BackupDate.ToString("yyyyMMddHHmmss") + ".backup");
                var workingDirectory = finalDirectory + ".copying";
                if (!Directory.Exists(finalDirectory) && Directory.Exists(workingDirectory))
                {
                    Directory.Move(workingDirectory, finalDirectory);
                    _checkpointObserver.Reached("publication-directory-renamed");
                }

                if (!Job.Published)
                {
                    Job.Published = true;
                    Job.StateCommitStep = StateCommitStep.Pending;
                    AppendJournalRecord("publication-pending", Job);
                    _checkpointObserver.Reached("published-job-committed");
                }
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
            long sourceBytes,
            Guid runId = default)
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
                    BackupDate = TruncateToSecond(backupDate),
                    RunId = runId
                };

                if (Job.RunId == Guid.Empty && runId != Guid.Empty)
                {
                    Job.RunId = runId;
                }

                Job.MaxSourceBytesPerPart = options.MaxSourceBytesPerPart;
                Job.MaxFilesPerPart = options.MaxFilesPerPart;
                Job.PlanFingerprint = planFingerprint;
                Job.TotalParts = totalParts;
                Job.TotalFiles = totalFiles;
                Job.SourceBytes = sourceBytes;
                Job.Published = preservePublishedState;
                // TotalParts is a count of active/nonempty parts. It is not an
                // allocation cursor: omitted parts intentionally leave gaps.
                // The durable reservation is advanced only by
                // ReservePartNumber after a plan has selected its stable
                // numbers.
                BackupDate = Job.BackupDate;
                AppendJournalRecord("job-updated", Job);

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
                    if (checkpoint == null ||
                        checkpoint.FormatVersion != BackupJobState.CurrentFormatVersion ||
                        checkpoint.PartNumber != partNumber)
                    {
                        throw new InvalidDataException(
                            $"Backup part checkpoint does not match part {partNumber}: '{path}'.");
                    }

                    return checkpoint;
                }
                catch (JsonException ex)
                {
                    throw new InvalidDataException(
                        $"Backup part checkpoint is malformed: '{path}'.", ex);
                }
            }
        }

        public BackupPartPlan? LoadPartPlan(int partNumber)
        {
            lock (_lock)
            {
                var path = GetPartPlanPath(partNumber);
                if (!File.Exists(path))
                {
                    return null;
                }

                try
                {
                    var plan = JsonSerializer.Deserialize<BackupPartPlan>(File.ReadAllText(path));
                    if (plan == null ||
                        plan.FormatVersion != BackupJobState.CurrentFormatVersion ||
                        plan.PartNumber != partNumber ||
                        (Job?.RunId is { } jobRunId && jobRunId != Guid.Empty &&
                         plan.RunId != Guid.Empty && plan.RunId != jobRunId))
                    {
                        throw new InvalidDataException(
                            $"Backup part plan does not match part {partNumber}: '{path}'.");
                    }

                    return plan;
                }
                catch (JsonException ex)
                {
                    throw new InvalidDataException(
                        $"Backup part plan is malformed: '{path}'.", ex);
                }
            }
        }

        public void SavePartPlan(BackupPartPlan plan)
        {
            lock (_lock)
            {
                Directory.CreateDirectory(_stagingDirectory);
                AppendJournalRecord("part-plan", plan);
                _checkpointObserver.Reached("part-plan-committed");
            }
        }

        public void SavePartCheckpoint(BackupPartCheckpoint checkpoint)
        {
            lock (_lock)
            {
                Directory.CreateDirectory(_stagingDirectory);
                AppendJournalRecord("part-checkpoint", checkpoint);
                _checkpointObserver.Reached("part-checkpoint-committed");
            }
        }

        public void DeletePartCheckpoint(int partNumber)
        {
            lock (_lock)
            {
                AppendJournalRecord("part-checkpoint-deleted", new { PartNumber = partNumber });
            }
        }

        public void DeletePartPlan(int partNumber)
        {
            lock (_lock)
            {
                AppendJournalRecord("part-plan-deleted", new { PartNumber = partNumber });
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
                Job.StateCommitStep = StateCommitStep.Pending;
                AppendJournalRecord("publication-pending", Job);
                _checkpointObserver.Reached("published-job-committed");
            }
        }

        public void ReservePartNumber(int partNumber)
        {
            lock (_lock)
            {
                if (Job == null)
                {
                    throw new InvalidOperationException("Backup job was not initialized.");
                }

                if (partNumber > Job.HighestReservedPartNumber)
                {
                    Job.HighestReservedPartNumber = partNumber;
                    AppendJournalRecord("part-reserved", new { PartNumber = partNumber });
                    _checkpointObserver.Reached("part-reservation-committed");
                }
            }
        }

        public void SaveStateCommitStep(StateCommitStep step)
        {
            lock (_lock)
            {
                if (Job == null)
                {
                    return;
                }

                Job.StateCommitStep = step;
                AppendJournalRecord("state-commit", new { Step = step });
                _checkpointObserver.Reached($"state-commit-{step}");
            }
        }

        public PublicationReceiptState? LoadPublicationReceipt()
        {
            lock (_lock)
            {
                if (!File.Exists(PublicationReceiptPath))
                {
                    return null;
                }

                try
                {
                    return JsonSerializer.Deserialize<PublicationReceiptState>(
                        File.ReadAllText(PublicationReceiptPath))
                        ?? throw new InvalidDataException("Publication receipt is empty.");
                }
                catch (JsonException ex)
                {
                    throw new InvalidDataException(
                        $"Publication receipt is malformed: '{PublicationReceiptPath}'.", ex);
                }
            }
        }

        public void SavePublicationReceipt(PublicationReceiptState receipt)
        {
            lock (_lock)
            {
                Directory.CreateDirectory(_stagingDirectory);
                AppendJournalRecord("publication-receipt", receipt);
                _checkpointObserver.Reached("publication-receipt-committed");
            }
        }

        public RetryScheduleState? LoadRetrySchedule()
        {
            lock (_lock)
            {
                return Job == null
                    ? null
                    : new RetryScheduleState(
                        Job.RetryCompletedRound,
                        Job.RetryActiveRound,
                        Job.RetryRoundInProgress,
                        Job.RetryNextAttemptUtc);
            }
        }

        public void SaveRetrySchedule(RetryScheduleComponent schedule)
        {
            lock (_lock)
            {
                if (Job == null)
                {
                    return;
                }

                Job.RetryCompletedRound = schedule.CompletedRound;
                Job.RetryActiveRound = schedule.ActiveRound;
                Job.RetryRoundInProgress = schedule.RoundInProgress;
                Job.RetryNextAttemptUtc = schedule.NextAttemptUtc;
                AppendJournalRecord("retry-schedule", schedule);
            }
        }

        public void ResetRun()
        {
            lock (_lock)
            {
                Job = null;
                BackupDate = null;
                RecoveredManifest = null;
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

        public void Checkpoint(string name)
        {
            _checkpointObserver.Reached(name);
        }

        public void SaveTransferredPart(int number, string archivePath, string indexPath)
        {
            lock (_lock)
            {
                AppendJournalRecord("part-transferred", new
                {
                    PartNumber = number, ArchivePath = archivePath, IndexPath = indexPath,
                    ArchiveSha256 = ComputeFileHash(archivePath), IndexSha256 = ComputeFileHash(indexPath)
                });
            }
        }

        private BackupJobState? LoadJob()
        {
            if (!File.Exists(JobPath))
            {
                return null;
            }

            try
            {
                var job = JsonSerializer.Deserialize<BackupJobState>(File.ReadAllText(JobPath));
                if (job == null ||
                    job.FormatVersion != BackupJobState.CurrentFormatVersion ||
                    !PathsEqual(job.SourceDirectory, _sourceDirectory) ||
                    !PathsEqual(job.BackupDestination, _backupDestination))
                {
                    throw new InvalidDataException("Backup job format or identity is not supported; state was preserved.");
                }

                return job;
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                throw new InvalidDataException($"Unable to read backup job state: '{JobPath}'.", ex);
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

            foreach (var path in Directory.EnumerateFiles(_stagingDirectory))
            {
                if (IsOwnedStagingArtifact(Path.GetFileName(path)))
                {
                    File.Delete(path);
                }
            }

            if (!Directory.EnumerateFileSystemEntries(_stagingDirectory).Any())
            {
                Directory.Delete(_stagingDirectory, false);
            }
        }

        private static bool IsOwnedStagingArtifact(string name)
        {
            if (name is "job.json" or "publication.receipt.json" or
                "components.journal" or "manifest.json")
            {
                return true;
            }

            if (name.StartsWith("job.json.", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("publication.receipt.json.", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!name.StartsWith("part-", StringComparison.OrdinalIgnoreCase) ||
                name.Length < 11)
            {
                return false;
            }

            var digits = name.Substring(5, 6);
            if (!digits.All(char.IsDigit))
            {
                return false;
            }

            var suffix = name.Substring(11);
            return suffix.Equals(".zip", StringComparison.OrdinalIgnoreCase) ||
                suffix.Equals(".zip.partial", StringComparison.OrdinalIgnoreCase) ||
                suffix.Equals(".zip.copying", StringComparison.OrdinalIgnoreCase) ||
                suffix.Equals(".index.json", StringComparison.OrdinalIgnoreCase) ||
                suffix.Equals(".plan.json", StringComparison.OrdinalIgnoreCase) ||
                suffix.Equals(".transfer.json", StringComparison.OrdinalIgnoreCase) ||
                suffix.StartsWith(".index.json.", StringComparison.OrdinalIgnoreCase) ||
                suffix.StartsWith(".plan.json.", StringComparison.OrdinalIgnoreCase);
        }

        private List<BackupJournalRecord> LoadAndValidateJournal()
        {
            _journalSequence = 0;
            var records = new List<BackupJournalRecord>();
            if (!File.Exists(JournalPath))
            {
                return records;
            }

            var text = File.ReadAllText(JournalPath);
            if (string.IsNullOrEmpty(text))
            {
                return records;
            }

            var lines = text.Split('\n');
            for (var index = 0; index < lines.Length; index++)
            {
                var line = lines[index].TrimEnd('\r');
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                BackupJournalRecord? record;
                try
                {
                    record = JsonSerializer.Deserialize<BackupJournalRecord>(line);
                }
                catch (JsonException ex)
                {
                    var isFinalUnterminatedLine = index == lines.Length - 1 &&
                        !text.EndsWith('\n');
                    if (isFinalUnterminatedLine)
                    {
                        // A process can be interrupted while appending the
                        // final JSON record. Only an actually unterminated
                        // JSON tail is recoverable; remove it before a later
                        // durable append. A complete record with a bad
                        // checksum must still fail below.
                        TruncateJournalAtLastCompleteLine(text);
                        break;
                    }

                    throw new InvalidDataException(
                        $"Backup component journal is corrupt at record {index + 1}: '{JournalPath}'.",
                        ex);
                }

                if (record == null ||
                    record.Sequence != _journalSequence + 1 ||
                    !string.Equals(record.Checksum,
                        ComputeJournalChecksum(record),
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"Backup component journal record {index + 1} has an invalid checksum or sequence: '{JournalPath}'.");
                }

                _journalSequence = record.Sequence;
                records.Add(record);
            }

            return records;
        }

        private void TruncateJournalAtLastCompleteLine(string text)
        {
            var lastNewline = text.LastIndexOf('\n');
            var completePrefix = lastNewline >= 0
                ? text[..(lastNewline + 1)]
                : string.Empty;
            var byteLength = Encoding.UTF8.GetByteCount(completePrefix);
            using var stream = new FileStream(
                JournalPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.Read);
            stream.SetLength(byteLength);
            stream.Flush(flushToDisk: true);
        }

        private void AppendJournalRecord<T>(string changeType, T payload)
        {
            Directory.CreateDirectory(_stagingDirectory);
            var record = new BackupJournalRecord
            {
                Sequence = ++_journalSequence,
                RunId = Job?.RunId ?? Guid.Empty,
                ChangeType = changeType,
                PayloadJson = JsonSerializer.Serialize(payload)
            };
            record.Checksum = ComputeJournalChecksum(record);

            using var stream = new FileStream(
                JournalPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.Read);
            // A complete, checksummed final JSON record may have reached disk
            // just before its newline. Separate it before the next append.
            if (stream.Length > 0)
            {
                stream.Position = stream.Length - 1;
                var last = stream.ReadByte();
                if (last != '\n') stream.WriteByte((byte)'\n');
            }
            stream.Position = stream.Length;
            using var writer = new StreamWriter(stream);
            writer.WriteLine(JsonSerializer.Serialize(record));
            writer.Flush();
            stream.Flush(flushToDisk: true);
            _checkpointObserver.Reached("journal-" + changeType + "-flushed");
            var views = new Dictionary<string, string?>();
            ApplyJournalChange(record, views);
            WriteViews(views);
        }

        // The same mechanical record application is used after append and on
        // replay. These are materialized DTO files, never workflow decisions.
        private void ReplayJournal(IReadOnlyList<BackupJournalRecord> records)
        {
            var views = new Dictionary<string, string?>();
            var recognized = new Dictionary<string, HashSet<string>>();
            foreach (var record in records)
            {
                ApplyJournalChange(record, views, recognized);
            }
            // Validate the entire inventory before replacing any materialized
            // record. Missing/stale views are replayable; altered ones are not.
            foreach (var pair in recognized)
            {
                if (pair.Key == JobPath || !File.Exists(pair.Key)) continue;
                var content = File.ReadAllText(pair.Key);
                if (!pair.Value.Contains(content))
                    throw new InvalidDataException($"Durable component record differs from its journal: '{pair.Key}'.");
            }
            WriteViews(views);
        }

        private static void SetView(string path, string? content, Dictionary<string, string?> views, Dictionary<string, HashSet<string>>? recognized)
        {
            views[path] = content;
            if (content != null && recognized != null)
            {
                if (!recognized.TryGetValue(path, out var versions))
                    recognized[path] = versions = new HashSet<string>();
                versions.Add(content);
            }
        }

        private void ApplyJournalChange(BackupJournalRecord record, Dictionary<string, string?> views, Dictionary<string, HashSet<string>>? recognized = null)
        {
            try
            {
                using var document = JsonDocument.Parse(record.PayloadJson);
                var payload = document.RootElement;
                int PartNumber()
                {
                    var number = payload.GetProperty("PartNumber").GetInt32();
                    return number > 0 ? number : throw new InvalidDataException("Invalid part reservation.");
                }
                switch (record.ChangeType)
                {
                    case "job-updated":
                    case "publication-pending":
                        var job = payload.Deserialize<BackupJobState>() ?? throw new InvalidDataException("Null job record.");
                        if (job.FormatVersion != BackupJobState.CurrentFormatVersion ||
                            !PathsEqual(job.SourceDirectory, _sourceDirectory) ||
                            !PathsEqual(job.BackupDestination, _backupDestination) ||
                            (record.RunId != Guid.Empty && job.RunId != record.RunId))
                            throw new InvalidDataException("Journal job identity is inconsistent.");
                        Job = job;
                        SetView(JobPath, JsonSerializer.Serialize(Job), views, recognized);
                        break;
                    case "part-plan":
                        SetView(GetPartPlanPath(PartNumber()), record.PayloadJson, views, recognized);
                        break;
                    case "part-checkpoint":
                        SetView(GetPartCheckpointPath(PartNumber()), record.PayloadJson, views, recognized);
                        break;
                    case "part-transferred":
                        SetView(Path.Combine(_stagingDirectory, $"part-{PartNumber():000000}.transfer.json"), record.PayloadJson, views, recognized);
                        break;
                    case "part-plan-deleted":
                        SetView(GetPartPlanPath(PartNumber()), null, views, recognized);
                        break;
                    case "part-checkpoint-deleted":
                        SetView(GetPartCheckpointPath(PartNumber()), null, views, recognized);
                        break;
                    case "publication-receipt":
                        SetView(PublicationReceiptPath, record.PayloadJson, views, recognized);
                        break;
                    case "part-reserved":
                        if (Job == null) throw new InvalidDataException("Reservation without a job.");
                        Job.HighestReservedPartNumber = Math.Max(Job.HighestReservedPartNumber, PartNumber());
                        SetView(JobPath, JsonSerializer.Serialize(Job), views, recognized);
                        break;
                    case "state-commit":
                        if (Job == null) throw new InvalidDataException("State commit without a job.");
                        var step = (StateCommitStep)payload.GetProperty("Step").GetInt32();
                        if (!Enum.IsDefined(step)) throw new InvalidDataException("Unknown state commit step.");
                        Job.StateCommitStep = step;
                        SetView(JobPath, JsonSerializer.Serialize(Job), views, recognized);
                        break;
                    case "retry-schedule":
                        if (Job == null) throw new InvalidDataException("Retry schedule without a job.");
                        var schedule = payload.Deserialize<RetryScheduleComponent>()!;
                        Job.RetryCompletedRound = schedule.CompletedRound;
                        Job.RetryActiveRound = schedule.ActiveRound;
                        Job.RetryRoundInProgress = schedule.RoundInProgress;
                        Job.RetryNextAttemptUtc = schedule.NextAttemptUtc;
                        SetView(JobPath, JsonSerializer.Serialize(Job), views, recognized);
                        break;
                    default:
                        throw new InvalidDataException($"Unknown component journal change '{record.ChangeType}'.");
                }
                if (Job != null && record.RunId != Guid.Empty && Job.RunId != record.RunId)
                    throw new InvalidDataException("Component journal mixes operation identities.");
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                throw new InvalidDataException($"Invalid journal payload at sequence {record.Sequence}.", ex);
            }
        }

        private static void WriteViews(Dictionary<string, string?> views)
        {
            foreach (var pair in views)
            {
                if (pair.Value == null) File.Delete(pair.Key);
                else if (!File.Exists(pair.Key) || File.ReadAllText(pair.Key) != pair.Value)
                {
                    using var document = JsonDocument.Parse(pair.Value);
                    SaveJsonAtomic(pair.Key, document.RootElement);
                }
            }
        }

        private static string ComputeJournalChecksum(BackupJournalRecord record)
        {
            var value = $"{record.Sequence}|{record.RunId:D}|{record.ChangeType}|{record.PayloadJson}";
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
        }

        private static void SaveJsonAtomic<T>(string path, T value)
        {
            var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(stream))
                {
                    writer.Write(JsonSerializer.Serialize(value));
                    writer.Flush();
                    stream.Flush(flushToDisk: true);
                }

                File.Move(temporaryPath, path, true);
            }
            finally
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch
                {
                    // Preserve the initiating persistence failure.
                }
            }
        }

        private static DateTime TruncateToSecond(DateTime value)
        {
            return new DateTime(value.Ticks - value.Ticks % TimeSpan.TicksPerSecond, value.Kind);
        }

        private static string ComputeFileHash(string path)
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream));
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

        public Guid RunId { get; set; }

        public string SourceDirectory { get; set; } = string.Empty;

        public string BackupDestination { get; set; } = string.Empty;

        public DateTime BackupDate { get; set; }

        public long MaxSourceBytesPerPart { get; set; }

        public int MaxFilesPerPart { get; set; }

        public string PlanFingerprint { get; set; } = string.Empty;

        public int TotalParts { get; set; }

        public int HighestReservedPartNumber { get; set; }

        public int TotalFiles { get; set; }

        public long SourceBytes { get; set; }

        public bool Published { get; set; }

        public StateCommitStep StateCommitStep { get; set; } = StateCommitStep.NotRequired;

        public int RetryCompletedRound { get; set; }

        public int RetryActiveRound { get; set; }

        public bool RetryRoundInProgress { get; set; }

        public DateTimeOffset? RetryNextAttemptUtc { get; set; }
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

        public string ArchiveSha256 { get; set; } = string.Empty;

        public List<BackupPartFile> Files { get; set; } = new();
    }

    public sealed class BackupPartPlan
    {
        public int FormatVersion { get; set; } = BackupJobState.CurrentFormatVersion;
        public Guid RunId { get; set; }
        public int PartNumber { get; set; }
        public DateTime BackupDate { get; set; }
        public BackupPartState State { get; set; } = BackupPartState.Planned;
        public string ArchiveFileName { get; set; } = string.Empty;
        public string Fingerprint { get; set; } = string.Empty;
        public long SourceBytes { get; set; }
        public List<BackupPartFile> Files { get; set; } = new();
    }

    public sealed class BackupJournalRecord
    {
        public long Sequence { get; set; }
        public Guid RunId { get; set; }
        public string ChangeType { get; set; } = string.Empty;
        public string PayloadJson { get; set; } = string.Empty;
        public string Checksum { get; set; } = string.Empty;
    }

    public sealed class PublicationReceiptState
    {
        public int FormatVersion { get; set; } = BackupJobState.CurrentFormatVersion;
        public Guid RunId { get; set; }
        public DateTime BackupDate { get; set; }
        public string WorkingDirectory { get; set; } = string.Empty;
        public string FinalDirectory { get; set; } = string.Empty;
        public string ManifestSha256 { get; set; } = string.Empty;
        public List<BackupManifestPart> Parts { get; set; } = new();
    }

    public sealed record RetryScheduleState(
        int CompletedRound,
        int ActiveRound,
        bool RoundInProgress,
        DateTimeOffset? NextAttemptUtc);
}
