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
    public sealed class BackupExecutionSystem : IEntitySetSystem
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
            BackupRunComponent? run = null;
            var parts = new List<PartEntity>();
            foreach (var entity in entities)
            {
                run ??= storage.GetComponent<BackupRunComponent>(entity);
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
                    PreparePart(item, run, progress);
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
                using var cancellation = new CancellationTokenSource();
                var transferTask = Task.Run(async () =>
                {
                    try
                    {
                        await TransferPartsAsync(
                            channel.Reader,
                            run,
                            progress,
                            localPartSlots,
                            cancellation.Token);
                    }
                    catch
                    {
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
                                CompressPart(item, progress);
                            }

                            channel.Writer
                                .WriteAsync(item, cancellation.Token)
                                .AsTask()
                                .GetAwaiter()
                                .GetResult();
                            slotOwnedByProducer = false;
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
                }

                progress.Report(force: true);
                var error = transferError ?? producerError;
                return error == null
                    ? Result.Success()
                    : Result.Fail($"Backup parts remain resumable after an execution failure: {error.Message}");
            }
            catch (Exception ex)
            {
                return Result.Fail($"Failed to execute backup parts: {ex.Message}");
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
            BackupProgressTracker progress)
        {
            var part = item.Part;
            var status = item.Status;
            var checkpoint = _runState.LoadPartCheckpoint(part.PartNumber);
            if (!CheckpointMatches(checkpoint, part))
            {
                InvalidatePart(item);
                return;
            }

            status.ArchiveBytes = checkpoint!.ArchiveBytes;
            if (File.Exists(status.DestinationArchivePath) &&
                new FileInfo(status.DestinationArchivePath).Length == checkpoint.ArchiveBytes)
            {
                status.State = BackupPartState.Transferred;
                File.Delete(status.LocalArchivePath);
                File.Delete(status.LocalArchivePath + ".partial");
                progress.ReuseTransferredPart(part);
                return;
            }

            File.Delete(status.DestinationArchivePath);
            File.Delete(status.DestinationArchivePath + ".copying");
            if (File.Exists(status.LocalArchivePath) &&
                new FileInfo(status.LocalArchivePath).Length == checkpoint.ArchiveBytes)
            {
                status.State = BackupPartState.Staged;
                progress.ReuseStagedPart(part);
                return;
            }

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
            BackupProgressTracker progress)
        {
            var part = item.Part;
            var status = item.Status;
            var partialPath = status.LocalArchivePath + ".partial";
            File.Delete(partialPath);
            File.Delete(status.LocalArchivePath);

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
                    foreach (var file in part.Files)
                    {
                        var entry = archive.CreateEntry(file.EntryName, GetCompressionLevel(file.SourcePath));
                        using var input = new FileStream(
                            file.SourcePath,
                            FileMode.Open,
                            FileAccess.Read,
                            FileShare.Read,
                            buffer.Length,
                            FileOptions.SequentialScan);
                        using var entryStream = entry.Open();

                        int bytesRead;
                        while ((bytesRead = input.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            entryStream.Write(buffer, 0, bytesRead);
                            progress.AddCompressedBytes(bytesRead);
                        }
                    }
                }

                File.Move(partialPath, status.LocalArchivePath, true);
                status.ArchiveBytes = new FileInfo(status.LocalArchivePath).Length;
                _runState.SavePartCheckpoint(new BackupPartCheckpoint
                {
                    PartNumber = part.PartNumber,
                    ArchiveFileName = part.ArchiveFileName,
                    Fingerprint = part.Fingerprint,
                    FileCount = part.Files.Count,
                    SourceBytes = part.SourceBytes,
                    ArchiveBytes = status.ArchiveBytes
                });
                status.State = BackupPartState.Staged;
                progress.CompleteCompressionPart(part);
            }
            catch
            {
                File.Delete(partialPath);
                throw;
            }
        }

        private async Task TransferPartsAsync(
            ChannelReader<PartEntity> reader,
            BackupRunComponent run,
            BackupProgressTracker progress,
            SemaphoreSlim localPartSlots,
            CancellationToken cancellationToken)
        {
            await foreach (var item in reader.ReadAllAsync(cancellationToken))
            {
                var part = item.Part;
                var status = item.Status;
                var copyingPath = status.DestinationArchivePath + ".copying";
                File.Delete(copyingPath);
                EnsureDestinationCapacity(run.BackupDestination, status.ArchiveBytes);

                try
                {
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

                    File.Move(copyingPath, status.DestinationArchivePath, true);
                    status.State = BackupPartState.Transferred;
                    File.Delete(status.LocalArchivePath);
                    progress.CompleteTransferPart(part);
                    _logger?.Log(
                        $"Backup Execution: transferred part {part.PartNumber}/{run.TotalParts} " +
                        $"to '{run.WorkingDirectory}'.");
                }
                catch
                {
                    File.Delete(copyingPath);
                    throw;
                }
                finally
                {
                    localPartSlots.Release();
                }
            }
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

                progressLogger.ReportProgress(
                    $"Backup parts: compress {compressionPercent:0.0}% ({_compressedParts}/{_totalParts}), " +
                    $"transfer {transferPercent:0.0}% ({_transferredParts}/{_totalParts}), ETA: {eta}");
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
