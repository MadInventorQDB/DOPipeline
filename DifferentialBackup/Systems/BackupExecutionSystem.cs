using DOPipeline.Entities;
using DOPipeline.Logging;
using DOPipeline.Storage;
using DOPipeline.Systems;
using DOPipeline.Utilities;
using DifferentialBackup.Components;
using DifferentialBackup.Utilities;
using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Threading.Channels;

namespace DifferentialBackup.Systems
{
    public sealed class BackupExecutionSystem : ICancellableEntitySetSystem
    {
        private readonly BackupRunState _runState;
        private readonly BackupBatchOptions _options;
        private readonly IPipelineLogger? _logger;

        public BackupExecutionSystem(
            BackupRunState runState,
            BackupBatchOptions options,
            IPipelineLogger? logger = null)
        {
            _runState = runState;
            _options = options;
            _logger = logger;
            _options.Validate();
        }

        public Result Execute(IReadOnlyList<Entity> entities, IComponentStorage storage)
        {
            return Execute(entities, storage, CancellationToken.None);
        }

        public Result Execute(
            IReadOnlyList<Entity> entities,
            IComponentStorage storage,
            CancellationToken externalCancellationToken)
        {
            externalCancellationToken.ThrowIfCancellationRequested();
            var operationRunId = entities
                .Select(entity => storage.GetComponent<OperationComponent>(entity))
                .FirstOrDefault()?.RunId ??
                storage.Query<OperationComponent>()
                    .Select(entity => storage.GetComponent<OperationComponent>(entity))
                    .FirstOrDefault()?.RunId ?? Guid.Empty;
            var operationComponent = entities
                .Select(entity => storage.GetComponent<OperationComponent>(entity))
                .FirstOrDefault(operation => operation != null)
                ?? storage.Query<OperationComponent>()
                    .Select(entity => storage.GetComponent<OperationComponent>(entity))
                    .FirstOrDefault();
            if (operationComponent?.Phase is OperationPhase.Finalizing or
                OperationPhase.PublishedPendingState or OperationPhase.Finished)
            {
                return Result.Success();
            }
            BackupRunComponent? run = null;
            var parts = new List<PartEntity>();
            foreach (var entity in entities)
            {
                var candidateRun = storage.GetComponent<BackupRunComponent>(entity);
                if (candidateRun != null &&
                    (operationRunId == Guid.Empty || candidateRun.RunId == operationRunId))
                {
                    run ??= candidateRun;
                }
                var part = storage.GetComponent<BackupPartComponent>(entity);
                if (part == null)
                {
                    continue;
                }

                var status = storage.GetComponent<BackupPartStatusComponent>(entity);
                if (status != null)
                {
                    parts.Add(new PartEntity(part, status));
                }
            }

            if (run == null)
            {
                return Result.Success();
            }

            parts = parts
                .Where(item => (item.Part.RunId == Guid.Empty || item.Part.RunId == run.RunId) &&
                    (item.Status.RunId == Guid.Empty || item.Status.RunId == run.RunId) &&
                    item.Status.State != BackupPartState.Omitted)
                .ToList();

            parts.Sort((left, right) => left.Part.PartNumber.CompareTo(right.Part.PartNumber));

            if (parts.Count != run.TotalParts)
            {
                return Result.Fail($"Expected {run.TotalParts} backup part entities but found {parts.Count}.");
            }

            try
            {
                if (TryUsePublishedBackup(run, parts, out var publishedError))
                {
                    _logger?.Log($"Backup Execution: recovered published backup '{run.FinalDirectory}'.");
                    return Result.Success();
                }

                if (publishedError != null)
                {
                    return Result.Fail(publishedError);
                }

                Directory.CreateDirectory(run.StagingDirectory);
                Directory.CreateDirectory(run.WorkingDirectory);

                var progress = new BackupProgressTracker(
                    run.SourceBytes,
                    run.TotalParts,
                    _logger);
                var pendingParts = new List<PartEntity>();

                foreach (var item in parts)
                {
                    PreparePart(item, run, progress, storage);
                    if (item.Status.State == BackupPartState.Transferred)
                    {
                        var index = TransferIndex(item);
                        _runState.SaveTransferredPart(item.Part.PartNumber, item.Status.DestinationArchivePath, index);
                    }
                    if (item.Status.State != BackupPartState.Transferred)
                    {
                        pendingParts.Add(item);
                    }
                }

                if (pendingParts.Count == 0)
                {
                    progress.Report(force: true);
                    return Result.Success();
                }

                EnsureLocalStagingCapacity(run, pendingParts);

                var channel = Channel.CreateBounded<PartEntity>(new BoundedChannelOptions(_options.TransferQueueCapacity)
                {
                    SingleReader = true,
                    SingleWriter = true,
                    FullMode = BoundedChannelFullMode.Wait
                });
                using var localPartSlots = new SemaphoreSlim(_options.TransferQueueCapacity + 1);
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(externalCancellationToken);
                Exception? firstWorkerFailure = null;
                var workerCancellation = false;
                var transferTask = Task.Run(async () =>
                {
                    try
                    {
                        await TransferPartsAsync(
                            channel.Reader,
                            run,
                            progress,
                            localPartSlots,
                            storage,
                            cancellation.Token);
                    }
                    catch (Exception ex)
                    {
                        if (ex is OperationCanceledException canceled &&
                            ((externalCancellationToken.IsCancellationRequested && canceled.CancellationToken == externalCancellationToken) ||
                             canceled.CancellationToken == cancellation.Token))
                        {
                            workerCancellation = true;
                        }
                        else
                        {
                            Interlocked.CompareExchange(ref firstWorkerFailure, ex, null);
                            try { _runState.Checkpoint("consumer-failure-recorded"); } catch { }
                        }
                        cancellation.Cancel();
                        throw;
                    }
                });

                Exception? producerError = null;
                try
                {
                    foreach (var item in pendingParts)
                    {
                        cancellation.Token.ThrowIfCancellationRequested();
                        localPartSlots.Wait(cancellation.Token);
                        var slotOwnedByProducer = true;
                        try
                        {
                            if (item.Status.State == BackupPartState.Planned)
                            {
                                _runState.Checkpoint("producer-part-starting");
                                CompressPart(item, progress, storage);
                            }

                            if (item.Status.State != BackupPartState.Omitted)
                            {
                                channel.Writer
                                    .WriteAsync(item, cancellation.Token)
                                    .AsTask()
                                    .GetAwaiter()
                                    .GetResult();
                                // Ownership transfers to the consumer only
                                // after the item has entered the queue.
                                slotOwnedByProducer = false;
                            }
                        }
                        finally
                        {
                            if (slotOwnedByProducer)
                            {
                                localPartSlots.Release();
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    producerError = ex;
                    if (ex is OperationCanceledException canceled &&
                        ((externalCancellationToken.IsCancellationRequested && canceled.CancellationToken == externalCancellationToken) ||
                         canceled.CancellationToken == cancellation.Token))
                    {
                        workerCancellation = true;
                    }
                    else
                    {
                        Interlocked.CompareExchange(ref firstWorkerFailure, ex, null);
                        try { _runState.Checkpoint("producer-failure-recorded"); } catch { }
                    }
                    cancellation.Cancel();
                }
                finally
                {
                    channel.Writer.TryComplete(producerError);
                }

                Exception? transferError = null;
                try
                {
                    transferTask.GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    transferError = ex;
                    if (ex is OperationCanceledException canceled &&
                        ((externalCancellationToken.IsCancellationRequested && canceled.CancellationToken == externalCancellationToken) ||
                         canceled.CancellationToken == cancellation.Token))
                    {
                        workerCancellation = true;
                    }
                    else
                    {
                        Interlocked.CompareExchange(ref firstWorkerFailure, ex, null);
                    }
                }

                try
                {
                    progress.Report(force: true);
                }
                catch
                {
                    // Progress is diagnostic output. It must not replace the
                    // first worker failure or turn successful work into a
                    // failure.
                }
                // Preserve the first non-cancellation failure from either
                // worker.  Linked cancellation from the sibling must never
                // replace that initiating error.
                if (firstWorkerFailure is not null)
                {
                    // A worker failure is authoritative even when external
                    // cancellation was requested while the sibling stopped.
                    // Keep the original exception for diagnostics and
                    // recovery classification.
                    return Result.Fail(firstWorkerFailure.Message, firstWorkerFailure);
                }

                if (externalCancellationToken.IsCancellationRequested ||
                    workerCancellation ||
                    producerError is OperationCanceledException ||
                    transferError is OperationCanceledException)
                {
                    return Result.Cancelled();
                }

                var activeParts = parts
                    .Where(item => item.Status.State != BackupPartState.Omitted && item.Part.Files.Count > 0)
                    .Select(item => item.Part)
                    .ToList();
                run.TotalParts = activeParts.Count;
                run.TotalFiles = activeParts.Sum(part => part.Files.Count);
                run.SourceBytes = activeParts.Sum(part => part.SourceBytes);
                run.PlanFingerprint = BackupFingerprint.ForParts(activeParts);
                _runState.CreateOrUpdateJob(
                    run.BackupDate,
                    _options,
                    run.PlanFingerprint,
                    run.TotalParts,
                    run.TotalFiles,
                    run.SourceBytes);
                return Result.Success();
            }
            catch (OperationCanceledException ex) when (externalCancellationToken.IsCancellationRequested && ex.CancellationToken == externalCancellationToken)
            {
                return Result.Cancelled();
            }
            catch (Exception ex)
            {
                return Result.Fail($"Failed to execute backup parts: {ex.Message}", ex);
            }
        }

        public Result Execute(Entity entity, IComponentStorage storage)
        {
            return Result.Fail("Backup part execution requires entity-set execution.");
        }

        private bool TryUsePublishedBackup(
            BackupRunComponent run,
            IReadOnlyList<PartEntity> parts,
            out string? error)
        {
            error = null;
            if (!Directory.Exists(run.FinalDirectory))
            {
                return false;
            }

            try
            {
                var manifestPath = Path.Combine(run.FinalDirectory, "manifest.json");
                var manifest = JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(manifestPath));
                if (manifest == null ||
                    manifest.FormatVersion != BackupManifest.CurrentFormatVersion ||
                    !string.Equals(manifest.PlanFingerprint, run.PlanFingerprint, StringComparison.Ordinal) ||
                    manifest.Parts.Count != parts.Count)
                {
                    error = $"Published backup directory is incompatible: '{run.FinalDirectory}'.";
                    return false;
                }

                var manifestParts = manifest.Parts.ToDictionary(part => part.PartNumber);
                foreach (var item in parts)
                {
                    var part = item.Part;
                    if (!manifestParts.TryGetValue(part.PartNumber, out var manifestPart) ||
                        !string.Equals(manifestPart.Fingerprint, part.Fingerprint, StringComparison.Ordinal))
                    {
                        error = $"Published backup part {part.PartNumber} does not match the current plan.";
                        return false;
                    }

                    var archivePath = Path.Combine(run.FinalDirectory, part.ArchiveFileName);
                    if (!File.Exists(archivePath) || new FileInfo(archivePath).Length != manifestPart.ArchiveBytes)
                    {
                        error = $"Published backup part is missing or incomplete: '{archivePath}'.";
                        return false;
                    }

                    if (!string.IsNullOrWhiteSpace(manifestPart.ArchiveSha256) &&
                        !string.Equals(manifestPart.ArchiveSha256, ComputeFileHash(archivePath), StringComparison.OrdinalIgnoreCase))
                    {
                        // A published part is an acknowledged artifact.  Do
                        // not fall back to current source bytes when its
                        // checksum no longer matches the manifest.
                        error = $"Published backup part checksum validation failed: '{archivePath}'.";
                        return false;
                    }

                    item.Status.State = BackupPartState.Transferred;
                    item.Status.DestinationArchivePath = archivePath;
                    item.Status.ArchiveBytes = manifestPart.ArchiveBytes;
                }

                return true;
            }
            catch (Exception ex)
            {
                error = $"Could not validate published backup '{run.FinalDirectory}': {ex.Message}";
                return false;
            }
        }

        private void PreparePart(
            PartEntity item,
            BackupRunComponent run,
            BackupProgressTracker progress,
            IComponentStorage storage)
        {
            var part = item.Part;
            var status = item.Status;
            var receipt = FindReceipt(part, storage);
            if (status.State == BackupPartState.Transferred &&
                File.Exists(status.DestinationArchivePath) &&
                receipt != null &&
                string.Equals(receipt.ArchiveSha256, ComputeFileHash(status.DestinationArchivePath), StringComparison.OrdinalIgnoreCase))
            {
                status.ArchiveBytes = new FileInfo(status.DestinationArchivePath).Length;
                progress.ReuseTransferredPart(part);
                return;
            }
            var checkpoint = _runState.LoadPartCheckpoint(part.PartNumber);
            if (status.State == BackupPartState.Transferred && checkpoint == null)
            {
                throw new InvalidDataException(
                    $"Acknowledged backup part {part.PartNumber} has no durable checkpoint.");
            }
            if (!CheckpointMatches(checkpoint, part))
            {
                if (status.State == BackupPartState.Transferred)
                {
                    throw new InvalidDataException(
                        $"Acknowledged backup part {part.PartNumber} does not match its durable checkpoint.");
                }

                InvalidatePart(item);
                return;
            }

            status.ArchiveBytes = checkpoint!.ArchiveBytes;
            var destinationExists = File.Exists(status.DestinationArchivePath);
            var destinationValid = destinationExists &&
                new FileInfo(status.DestinationArchivePath).Length == checkpoint.ArchiveBytes &&
                (string.IsNullOrWhiteSpace(checkpoint.ArchiveSha256) ||
                 string.Equals(checkpoint.ArchiveSha256, ComputeFileHash(status.DestinationArchivePath), StringComparison.OrdinalIgnoreCase));
            var localExists = File.Exists(status.LocalArchivePath);
            var localValid = localExists &&
                new FileInfo(status.LocalArchivePath).Length == checkpoint.ArchiveBytes &&
                (string.IsNullOrWhiteSpace(checkpoint.ArchiveSha256) ||
                 string.Equals(checkpoint.ArchiveSha256, ComputeFileHash(status.LocalArchivePath), StringComparison.OrdinalIgnoreCase));

            if (destinationExists && destinationValid)
            {
                status.State = BackupPartState.Transferred;
                SetReceiptFromCheckpoint(item, checkpoint, storage);
                File.Delete(status.LocalArchivePath);
                File.Delete(status.LocalArchivePath + ".partial");
                progress.ReuseTransferredPart(part);
                return;
            }

            if (localValid)
            {
                status.State = BackupPartState.Staged;
                SetReceiptFromCheckpoint(item, checkpoint, storage);
                progress.ReuseStagedPart(part);
                return;
            }

            if (!string.IsNullOrWhiteSpace(checkpoint.ArchiveSha256))
            {
                throw new InvalidDataException(
                    $"Acknowledged backup part {part.PartNumber} has no valid staged or transferred copy.");
            }

            File.Delete(status.DestinationArchivePath);
            File.Delete(status.DestinationArchivePath + ".copying");
            InvalidatePart(item);
        }

        private static bool CheckpointMatches(
            BackupPartCheckpoint? checkpoint,
            BackupPartComponent part)
        {
            return checkpoint != null &&
                   checkpoint.PartNumber == part.PartNumber &&
                   checkpoint.FileCount == part.Files.Count &&
                   checkpoint.SourceBytes == part.SourceBytes &&
                   checkpoint.ArchiveBytes > 0 &&
                   string.Equals(checkpoint.ArchiveFileName, part.ArchiveFileName, StringComparison.Ordinal) &&
                   string.Equals(checkpoint.Fingerprint, part.Fingerprint, StringComparison.Ordinal);
        }

        private void InvalidatePart(PartEntity item)
        {
            var part = item.Part;
            var status = item.Status;
            _runState.DeletePartCheckpoint(part.PartNumber);
            File.Delete(status.LocalArchivePath);
            File.Delete(status.LocalArchivePath + ".partial");
            File.Delete(status.DestinationArchivePath);
            File.Delete(status.DestinationArchivePath + ".copying");
            status.State = BackupPartState.Planned;
            status.ArchiveBytes = 0;
        }

        private void CompressPart(
            PartEntity item,
            BackupProgressTracker progress,
            IComponentStorage storage)
        {
            var part = item.Part;
            var status = item.Status;
            var remaining = part.Files.ToList();
            var partialPath = status.LocalArchivePath + ".partial";

            while (remaining.Count > 0)
            {
                File.Delete(partialPath);
                File.Delete(status.LocalArchivePath);
                BackupPartFile? failedFile = null;
                Exception? sourceFailure = null;

                try
                {
                    using (var output = new FileStream(
                        partialPath,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None,
                        1024 * 1024,
                        FileOptions.SequentialScan))
                    using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: false))
                    {
                        var buffer = new byte[1024 * 1024];
                        foreach (var file in remaining)
                        {
                            FileStream input;
                            try
                            {
                                // Open before creating the ZIP entry. An open
                                // failure therefore cannot leave a truncated
                                // entry in the archive.
                                input = new FileStream(
                                    file.SourcePath,
                                    FileMode.Open,
                                    FileAccess.Read,
                                    FileShare.Read,
                                    buffer.Length,
                                    FileOptions.SequentialScan);
                            }
                            catch (Exception ex)
                            {
                                failedFile = file;
                                sourceFailure = ex;
                                break;
                            }

                            using (input)
                            {
                                var entry = archive.CreateEntry(file.EntryName, GetCompressionLevel(file.SourcePath));
                                using var entryStream = entry.Open();
                                using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(
                                    System.Security.Cryptography.HashAlgorithmName.SHA256);
                                long length = 0;
                                var readFailure = false;
                                while (true)
                                {
                                    int bytesRead;
                                    try
                                    {
                                        bytesRead = input.Read(buffer, 0, buffer.Length);
                                    }
                                    catch (Exception ex)
                                    {
                                        failedFile = file;
                                        sourceFailure = ex;
                                        readFailure = true;
                                        break;
                                    }

                                    if (bytesRead == 0)
                                    {
                                        break;
                                    }

                                    entryStream.Write(buffer, 0, bytesRead);
                                    hash.AppendData(buffer, 0, bytesRead);
                                    length += bytesRead;
                                    progress.AddCompressedBytes(bytesRead);
                                }

                                if (readFailure)
                                {
                                    break;
                                }

                                var observedHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                                var hasConcreteExpectedHash = file.Hash.Length == 64 &&
                                    file.Hash.All(character => Uri.IsHexDigit(character));
                                if (length != file.Length ||
                                    (hasConcreteExpectedHash && !string.Equals(observedHash, file.Hash, StringComparison.OrdinalIgnoreCase)))
                                {
                                    failedFile = file;
                                    sourceFailure = new IOException(
                                        $"Source file changed while it was captured: '{file.SourcePath}'.");
                                    break;
                                }
                            }
                        }
                    }

                    if (failedFile == null)
                    {
                        File.Move(partialPath, status.LocalArchivePath, true);
                        _runState.Checkpoint("staged-output-closed");
                        status.ArchiveBytes = new FileInfo(status.LocalArchivePath).Length;
                        part.Files = remaining.ToArray();
                        part.SourceBytes = remaining.Sum(file => file.Length);
                        part.Fingerprint = BackupFingerprint.ForFiles(remaining);
                        _runState.SavePartCheckpoint(new BackupPartCheckpoint
                        {
                            PartNumber = part.PartNumber,
                            ArchiveFileName = part.ArchiveFileName,
                            Fingerprint = part.Fingerprint,
                            FileCount = part.Files.Count,
                            SourceBytes = part.SourceBytes,
                            ArchiveBytes = status.ArchiveBytes,
                            ArchiveSha256 = ComputeFileHash(status.LocalArchivePath),
                            Files = part.Files.ToList()
                        });
                        status.State = BackupPartState.Staged;
                        MarkCaptured(part, storage);
                        SetReceipt(item, storage);
                        progress.CompleteCompressionPart(part);
                        return;
                    }
                }
                catch
                {
                    try
                    {
                        File.Delete(partialPath);
                    }
                    catch
                    {
                        // Preserve the initiating capture/output failure.
                    }
                    throw;
                }

                File.Delete(partialPath);
                if (failedFile == null)
                {
                    throw new IOException($"Could not capture backup part {part.PartNumber}.");
                }

                    MarkDeferred(
                        failedFile,
                        sourceFailure ?? new IOException("Source capture failed."),
                        part.RunId,
                        storage);
                remaining.RemoveAll(candidate => string.Equals(
                    candidate.SourcePath,
                    failedFile.SourcePath,
                    StringComparison.OrdinalIgnoreCase));
            }

            part.Files = Array.Empty<BackupPartFile>();
            part.SourceBytes = 0;
            part.Fingerprint = BackupFingerprint.ForFiles(Array.Empty<BackupPartFile>());
            status.State = BackupPartState.Omitted;
            status.ArchiveBytes = 0;
            _runState.DeletePartCheckpoint(part.PartNumber);
            var omittedPlan = _runState.LoadPartPlan(part.PartNumber);
            if (omittedPlan != null)
            {
                omittedPlan.State = BackupPartState.Omitted;
                _runState.SavePartPlan(omittedPlan);
            }
        }

        private static void MarkDeferred(
            BackupPartFile file,
            Exception exception,
            Guid runId,
            IComponentStorage storage)
        {
            foreach (var entity in storage.Query<FileWorkComponent>())
            {
                var work = storage.GetComponent<FileWorkComponent>(entity);
                if (work == null || work.RunId != runId ||
                    !string.Equals(work.SourcePath, file.SourcePath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                work.State = FileWorkState.Deferred;
                work.PartNumber = null;
                storage.SetComponent(entity, work);
                var now = DateTimeOffset.UtcNow;
                var issue = storage.GetComponent<BackupIssueComponent>(entity) ?? new BackupIssueComponent
                {
                    RunId = work.RunId,
                    StableKey = work.StableKey,
                    Path = file.SourcePath,
                    Stage = "Capture",
                    Category = "SourceChanged",
                    FirstOccurrenceUtc = now,
                    Retryable = true
                };
                issue.AttemptCount++;
                issue.LastOccurrenceUtc = now;
                issue.ExceptionType ??= exception.GetType().FullName;
                issue.OriginalMessage ??= exception.Message;
                issue.Resolved = false;
                storage.SetComponent(entity, issue);
                return;
            }

            var created = new Entity();
            var nowForCreated = DateTimeOffset.UtcNow;
            storage.SetComponent(created, new FilePathComponent { FilePath = file.SourcePath });
            storage.SetComponent(created, new FileWorkComponent
            {
                RunId = runId,
                SourcePath = file.SourcePath,
                StableKey = file.SourcePath,
                State = FileWorkState.Deferred
            });
            storage.SetComponent(created, new BackupIssueComponent
            {
                RunId = runId,
                Path = file.SourcePath,
                StableKey = file.SourcePath,
                Stage = "Capture",
                Category = "SourceRead",
                OriginalMessage = exception.Message,
                ExceptionType = exception.GetType().FullName,
                AttemptCount = 1,
                FirstOccurrenceUtc = nowForCreated,
                LastOccurrenceUtc = nowForCreated,
                Retryable = true
            });
        }

        private static void MarkCaptured(BackupPartComponent part, IComponentStorage storage)
        {
            foreach (var file in part.Files)
            {
                foreach (var entity in storage.Query<FileWorkComponent>())
                {
                    var work = storage.GetComponent<FileWorkComponent>(entity);
                    if (work == null || work.RunId != part.RunId ||
                        !string.Equals(work.SourcePath, file.SourcePath, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    work.State = FileWorkState.Captured;
                    storage.SetComponent(entity, work);
                    var issue = storage.GetComponent<BackupIssueComponent>(entity);
                    if (issue != null)
                    {
                        issue.Resolved = true;
                        storage.SetComponent(entity, issue);
                    }
                    break;
                }
            }
        }

        private async Task TransferPartsAsync(
            ChannelReader<PartEntity> reader,
            BackupRunComponent run,
            BackupProgressTracker progress,
            SemaphoreSlim localPartSlots,
            IComponentStorage storage,
            CancellationToken cancellationToken)
        {
            await foreach (var item in reader.ReadAllAsync(cancellationToken))
            {
                var part = item.Part;
                var status = item.Status;
                var copyingPath = status.DestinationArchivePath + ".copying";

                try
                {
                    _runState.Checkpoint("transfer-part-starting");
                    File.Delete(copyingPath);
                    EnsureDestinationCapacity(run.BackupDestination, status.ArchiveBytes);
                    await using (var input = new FileStream(
                        status.LocalArchivePath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        _options.CopyBufferSize,
                        FileOptions.Asynchronous | FileOptions.SequentialScan))
                    await using (var output = new FileStream(
                        copyingPath,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None,
                        _options.CopyBufferSize,
                        FileOptions.Asynchronous | FileOptions.SequentialScan))
                    {
                        var buffer = new byte[_options.CopyBufferSize];
                        long copiedBytes = 0;
                        int bytesRead;
                        while ((bytesRead = await input.ReadAsync(buffer, cancellationToken)) > 0)
                        {
                            await output.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                            copiedBytes += bytesRead;
                            progress.SetPartTransferProgress(part, copiedBytes, status.ArchiveBytes);
                        }

                        await output.FlushAsync(cancellationToken);
                        output.Flush(flushToDisk: true);
                    }

                    if (new FileInfo(copyingPath).Length != status.ArchiveBytes)
                    {
                        throw new IOException($"Transferred part length does not match for '{part.ArchiveFileName}'.");
                    }

                    var receipt = FindReceipt(part, storage);
                    if (receipt != null && !string.IsNullOrWhiteSpace(receipt.ArchiveSha256) &&
                        !string.Equals(receipt.ArchiveSha256, ComputeFileHash(copyingPath), StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException($"Transferred part checksum does not match for '{part.ArchiveFileName}'.");
                    }

                    File.Move(copyingPath, status.DestinationArchivePath, true);
                    _runState.Checkpoint("destination-archive-renamed");
                    var destinationIndex = TransferIndex(item);
                    _runState.SaveTransferredPart(part.PartNumber, status.DestinationArchivePath, destinationIndex);
                    status.State = BackupPartState.Transferred;
                    // A receipt is the durable acknowledgement that the
                    // bytes described by the part were captured.  Create it
                    // before deleting the local copy so a recovered world can
                    // validate the transfer without rereading source files.
                    SetReceipt(item, storage);
                    _runState.Checkpoint("part-transfer-committed");
                    File.Delete(status.LocalArchivePath);
                    progress.CompleteTransferPart(part);
                    _logger?.Log(
                        $"Backup Execution: transferred part {part.PartNumber}/{run.TotalParts} " +
                        $"to '{run.WorkingDirectory}'.");
                }
                catch
                {
                    try
                    {
                        File.Delete(copyingPath);
                    }
                    catch
                    {
                        // Preserve the initiating transfer failure.
                    }
                    throw;
                }
                finally
                {
                    localPartSlots.Release();
                }
            }
        }

        private void SetReceipt(PartEntity item, IComponentStorage storage)
        {
            var indexPath = _runState.GetPartCheckpointPath(item.Part.PartNumber);
            var receipt = new PartReceiptComponent
            {
                RunId = item.Part.RunId,
                PartNumber = item.Part.PartNumber,
                Files = item.Part.Files,
                Fingerprint = item.Part.Fingerprint,
                ArchiveLength = item.Status.ArchiveBytes,
                ArchiveSha256 = ComputeFileHash(item.Status.LocalArchivePath),
                IndexLength = File.Exists(indexPath) ? new FileInfo(indexPath).Length : 0,
                IndexSha256 = File.Exists(indexPath) ? ComputeFileHash(indexPath) : string.Empty
            };
            foreach (var entity in storage.Query<BackupPartComponent>())
            {
                if (ReferenceEquals(storage.GetComponent<BackupPartComponent>(entity), item.Part))
                {
                    storage.SetComponent(entity, receipt);
                    return;
                }
            }
        }

        private string TransferIndex(PartEntity item)
        {
            var source = _runState.GetPartCheckpointPath(item.Part.PartNumber);
            var destination = Path.Combine(Path.GetDirectoryName(item.Status.DestinationArchivePath)!, Path.GetFileName(source));
            var expected = ComputeFileHash(source);
            if (File.Exists(destination))
            {
                if (!string.Equals(expected, ComputeFileHash(destination), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Transferred index checksum is invalid: '{destination}'.");
                return destination;
            }
            var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".copying";
            try
            {
                File.Copy(source, temporary);
                using (var stream = new FileStream(temporary, FileMode.Open, FileAccess.Write, FileShare.None))
                    stream.Flush(flushToDisk: true);
                if (ComputeFileHash(temporary) != expected)
                    throw new InvalidDataException("Transferred index checksum mismatch.");
                File.Move(temporary, destination);
            }
            finally
            {
                try { File.Delete(temporary); } catch { }
            }
            return destination;
        }

        private void SetReceiptFromCheckpoint(
            PartEntity item,
            BackupPartCheckpoint checkpoint,
            IComponentStorage storage)
        {
            var indexPath = _runState.GetPartCheckpointPath(item.Part.PartNumber);
            foreach (var entity in storage.Query<BackupPartComponent>())
            {
                if (!ReferenceEquals(storage.GetComponent<BackupPartComponent>(entity), item.Part))
                {
                    continue;
                }

                storage.SetComponent(entity, new PartReceiptComponent
                {
                    RunId = item.Part.RunId,
                    PartNumber = item.Part.PartNumber,
                    Files = checkpoint.Files.Count > 0 ? checkpoint.Files : item.Part.Files,
                    Fingerprint = checkpoint.Fingerprint,
                    ArchiveLength = checkpoint.ArchiveBytes,
                    ArchiveSha256 = checkpoint.ArchiveSha256,
                    IndexLength = File.Exists(indexPath) ? new FileInfo(indexPath).Length : 0,
                    IndexSha256 = File.Exists(indexPath) ? ComputeFileHash(indexPath) : string.Empty
                });
                return;
            }
        }

        private static PartReceiptComponent? FindReceipt(BackupPartComponent part, IComponentStorage storage)
        {
            foreach (var entity in storage.Query<BackupPartComponent>())
            {
                if (ReferenceEquals(storage.GetComponent<BackupPartComponent>(entity), part))
                {
                    return storage.GetComponent<PartReceiptComponent>(entity);
                }
            }

            return null;
        }

        private static string ComputeFileHash(string path)
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
        }

        private void EnsureLocalStagingCapacity(
            BackupRunComponent run,
            IReadOnlyList<PartEntity> parts)
        {
            var plannedParts = parts
                .Where(item => item.Status.State == BackupPartState.Planned)
                .ToList();
            if (plannedParts.Count == 0)
            {
                return;
            }

            var largestPart = plannedParts.Max(item => item.Part.SourceBytes);
            var boundedPartCount = Math.Min(plannedParts.Count, _options.TransferQueueCapacity + 1);
            var requiredBytes = checked(largestPart * boundedPartCount);
            EnsureDriveCapacity(run.StagingDirectory, requiredBytes, "local staging");
        }

        private static void EnsureDestinationCapacity(string destination, long requiredBytes)
        {
            EnsureDriveCapacity(destination, requiredBytes, "backup destination");
        }

        private static void EnsureDriveCapacity(string path, long requiredBytes, string label)
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrWhiteSpace(root))
            {
                return;
            }

            var drive = new DriveInfo(root);
            if (drive.IsReady && drive.AvailableFreeSpace < requiredBytes)
            {
                throw new IOException(
                    $"Not enough free space in {label}. " +
                    $"Required: {requiredBytes / 1024d / 1024d:0.0} MB; " +
                    $"available: {drive.AvailableFreeSpace / 1024d / 1024d:0.0} MB.");
            }
        }

        private static CompressionLevel GetCompressionLevel(string filePath)
        {
            return PreCompressedExtensions.Contains(Path.GetExtension(filePath))
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

        private sealed record PartEntity(
            BackupPartComponent Part,
            BackupPartStatusComponent Status);

        private sealed class BackupProgressTracker
        {
            private readonly object _lock = new();
            private readonly long _totalSourceBytes;
            private readonly int _totalParts;
            private readonly IPipelineLogger? _logger;
            private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
            private long _compressedBytes;
            private long _transferredSourceBytes;
            private long _compressedSessionBytes;
            private long _transferredSessionBytes;
            private long _currentTransferSourceBytes;
            private int _compressedParts;
            private int _transferredParts;
            private long _lastReportTimestamp;

            public BackupProgressTracker(long totalSourceBytes, int totalParts, IPipelineLogger? logger)
            {
                _totalSourceBytes = totalSourceBytes;
                _totalParts = totalParts;
                _logger = logger;
            }

            public void ReuseTransferredPart(BackupPartComponent part)
            {
                lock (_lock)
                {
                    _compressedBytes += part.SourceBytes;
                    _transferredSourceBytes += part.SourceBytes;
                    _compressedParts++;
                    _transferredParts++;
                }
            }

            public void ReuseStagedPart(BackupPartComponent part)
            {
                lock (_lock)
                {
                    _compressedBytes += part.SourceBytes;
                    _compressedParts++;
                }
            }

            public void AddCompressedBytes(int bytes)
            {
                lock (_lock)
                {
                    _compressedBytes += bytes;
                    _compressedSessionBytes += bytes;
                    ReportLocked(force: false);
                }
            }

            public void CompleteCompressionPart(BackupPartComponent part)
            {
                lock (_lock)
                {
                    _compressedParts++;
                    ReportLocked(force: false);
                }
            }

            public void SetPartTransferProgress(
                BackupPartComponent part,
                long copiedArchiveBytes,
                long totalArchiveBytes)
            {
                lock (_lock)
                {
                    var previous = _currentTransferSourceBytes;
                    _currentTransferSourceBytes = totalArchiveBytes == 0
                        ? part.SourceBytes
                        : (long)(part.SourceBytes * (copiedArchiveBytes / (double)totalArchiveBytes));
                    _transferredSessionBytes += Math.Max(0, _currentTransferSourceBytes - previous);
                    ReportLocked(force: false);
                }
            }

            public void CompleteTransferPart(BackupPartComponent part)
            {
                lock (_lock)
                {
                    _transferredSourceBytes += part.SourceBytes;
                    _currentTransferSourceBytes = 0;
                    _transferredParts++;
                    ReportLocked(force: false);
                }
            }

            public void Report(bool force)
            {
                lock (_lock)
                {
                    ReportLocked(force);
                }
            }

            private void ReportLocked(bool force)
            {
                if (_logger is not IProgressLogger progressLogger)
                {
                    return;
                }

                var now = Stopwatch.GetTimestamp();
                if (!force &&
                    _lastReportTimestamp != 0 &&
                    Stopwatch.GetElapsedTime(_lastReportTimestamp, now) < TimeSpan.FromSeconds(1))
                {
                    return;
                }

                _lastReportTimestamp = now;
                var compressionPercent = Percent(_compressedBytes, _compressedParts);
                var transferBytes = _transferredSourceBytes + _currentTransferSourceBytes;
                var transferPercent = Percent(transferBytes, _transferredParts);
                var eta = CalculateEta(
                    _totalSourceBytes - Math.Min(_totalSourceBytes, _compressedBytes),
                    _compressedSessionBytes,
                    _totalSourceBytes - Math.Min(_totalSourceBytes, transferBytes),
                    _transferredSessionBytes);

                try
                {
                    progressLogger.ReportProgress(
                        $"Backup parts: compress {compressionPercent:0.0}% ({_compressedParts}/{_totalParts}), " +
                        $"transfer {transferPercent:0.0}% ({_transferredParts}/{_totalParts}), ETA: {eta}");
                }
                catch
                {
                    // Progress is diagnostic output only.
                }
            }

            private double Percent(long bytes, int parts)
            {
                return _totalSourceBytes > 0
                    ? Math.Min(100, bytes * 100d / _totalSourceBytes)
                    : Math.Min(100, parts * 100d / _totalParts);
            }

            private string CalculateEta(
                long remainingCompressionBytes,
                long compressedSessionBytes,
                long remainingTransferBytes,
                long transferredSessionBytes)
            {
                var compressionEta = Estimate(remainingCompressionBytes, compressedSessionBytes);
                var transferEta = Estimate(remainingTransferBytes, transferredSessionBytes);
                var eta = compressionEta > transferEta ? compressionEta : transferEta;
                return eta == TimeSpan.MaxValue ? "--:--:--" : FormatDuration(eta);
            }

            private TimeSpan Estimate(long remainingBytes, long completedSessionBytes)
            {
                if (remainingBytes <= 0)
                {
                    return TimeSpan.Zero;
                }

                if (completedSessionBytes <= 0 || _stopwatch.Elapsed <= TimeSpan.Zero)
                {
                    return TimeSpan.MaxValue;
                }

                var seconds = remainingBytes * _stopwatch.Elapsed.TotalSeconds / completedSessionBytes;
                return seconds >= TimeSpan.MaxValue.TotalSeconds
                    ? TimeSpan.MaxValue
                    : TimeSpan.FromSeconds(seconds);
            }

            private static string FormatDuration(TimeSpan duration)
            {
                var totalHours = (long)duration.TotalHours;
                return $"{totalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}";
            }
        }
    }
}
