using DOPipeline.Entities;
using DOPipeline.Logging;
using DOPipeline.Storage;
using DOPipeline.Systems;
using DOPipeline.Utilities;
using DifferentialBackup.Components;
using DifferentialBackup.Utilities;
using System.Collections.Concurrent;
using System.Text.Json;

namespace DifferentialBackup.Systems
{
    public sealed class BackupPublicationSystem : IEntitySetSystem
    {
        private readonly ConcurrentDictionary<string, string> _fileHashes;
        private readonly HashSet<DateTime> _backupDates;
        private readonly BackupRunState _runState;
        private readonly IPipelineLogger? _logger;
        private readonly object _backupDatesLock = new();

        public BackupPublicationSystem(
            ConcurrentDictionary<string, string> fileHashes,
            HashSet<DateTime> backupDates,
            BackupRunState runState,
            IPipelineLogger? logger = null)
        {
            _fileHashes = fileHashes;
            _backupDates = backupDates;
            _runState = runState;
            _logger = logger;
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

            try
            {
                if (parts.Count != run.TotalParts ||
                    parts.Any(item => item.Status.State != BackupPartState.Transferred))
                {
                    return Result.Fail("Backup cannot be published because one or more parts are incomplete.");
                }

                if (!Directory.Exists(run.FinalDirectory))
                {
                    if (!Directory.Exists(run.WorkingDirectory))
                    {
                        return Result.Fail("Backup working directory is missing.");
                    }

                    foreach (var item in parts)
                    {
                        var archivePath = item.Status.DestinationArchivePath;
                        if (!File.Exists(archivePath) ||
                            new FileInfo(archivePath).Length != item.Status.ArchiveBytes)
                        {
                            return Result.Fail($"Backup part is missing or incomplete: '{archivePath}'.");
                        }
                    }

                    var manifest = new BackupManifest
                    {
                        SourceDirectory = run.SourceDirectory,
                        BackupDate = run.BackupDate,
                        PlanFingerprint = run.PlanFingerprint,
                        FileCount = run.TotalFiles,
                        SourceBytes = run.SourceBytes,
                        Parts = parts.Select(item => new BackupManifestPart
                        {
                            PartNumber = item.Part.PartNumber,
                            ArchiveFileName = item.Part.ArchiveFileName,
                            Fingerprint = item.Part.Fingerprint,
                            FileCount = item.Part.Files.Count,
                            SourceBytes = item.Part.SourceBytes,
                            ArchiveBytes = item.Status.ArchiveBytes
                        }).ToList()
                    };

                    WriteManifest(manifest, run);
                    Directory.Move(run.WorkingDirectory, run.FinalDirectory);
                }

                _runState.MarkPublished();

                foreach (var item in parts)
                {
                    foreach (var file in item.Part.Files)
                    {
                        _fileHashes[file.SourcePath] = file.Hash;
                    }
                }

                lock (_backupDatesLock)
                {
                    _backupDates.Add(run.BackupDate);
                }

                _logger?.Log($"Backup Publication: finalized '{run.FinalDirectory}'.");
                return Result.Success();
            }
            catch (Exception ex)
            {
                return Result.Fail($"Failed to publish backup: {ex.Message}");
            }
        }

        public Result Execute(Entity entity, IComponentStorage storage)
        {
            return Result.Fail("Backup publication requires entity-set execution.");
        }

        private void WriteManifest(BackupManifest manifest, BackupRunComponent run)
        {
            var localTemporaryPath = _runState.FinalManifestStagingPath + ".tmp";
            File.WriteAllText(localTemporaryPath, JsonSerializer.Serialize(manifest));
            File.Move(localTemporaryPath, _runState.FinalManifestStagingPath, true);

            var destinationPath = Path.Combine(run.WorkingDirectory, "manifest.json");
            var destinationTemporaryPath = destinationPath + ".copying";
            File.Delete(destinationTemporaryPath);
            File.Copy(_runState.FinalManifestStagingPath, destinationTemporaryPath);

            using (var stream = new FileStream(
                destinationTemporaryPath,
                FileMode.Open,
                FileAccess.Write,
                FileShare.None))
            {
                stream.Flush(flushToDisk: true);
            }

            if (new FileInfo(destinationTemporaryPath).Length !=
                new FileInfo(_runState.FinalManifestStagingPath).Length)
            {
                throw new IOException("Transferred backup manifest length does not match.");
            }

            File.Move(destinationTemporaryPath, destinationPath, true);
        }

        private sealed record PartEntity(
            BackupPartComponent Part,
            BackupPartStatusComponent Status);
    }
}
