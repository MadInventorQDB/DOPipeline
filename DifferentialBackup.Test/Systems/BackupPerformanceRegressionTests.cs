using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DifferentialBackup.Components;
using DifferentialBackup.Pipeline;
using DifferentialBackup.Systems;
using DifferentialBackup.Test.Helpers;
using DifferentialBackup.Utilities;
using DOPipeline.Components;
using DOPipeline.Entities;
using DOPipeline.Logging;
using DOPipeline.Storage;
using Xunit.Abstractions;

namespace DifferentialBackup.Test.Systems;

public sealed class BackupPerformanceRegressionTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "BackupPerformance-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private static ComponentStorage CreateBackupWorld(string source, string destination)
    {
        var world = new ComponentStorage();
        var entity = new Entity();
        var runId = Guid.NewGuid();
        world.SetComponent(entity, new OperationComponent { RunId = runId, OperationType = "backup",
            SourceDirectory = source, BackupDestination = destination, Phase = OperationPhase.InitialPass,
            InitializationSucceeded = true });
        world.SetComponent(entity, new RetryScheduleComponent { RunId = runId });
        return world;
    }

    private sealed class ThrowAtCheckpointObserver(string checkpoint) : ICheckpointObserver
    {
        public void Reached(string value) { if (value == checkpoint) throw new IOException("Injected interruption: " + value); }
    }

    [Theory]
    [InlineData(64)]
    [InlineData(512)]
    public void PublicationAndTransferHaveBoundedArchiveReadAndStateWriteBudgets(int fileCount)
    {
        var fixture = PerformanceCorpus(fileCount, 128 * 1024);
        var metrics = new PerformanceMetrics(fixture.Destination);
        var state = PerformanceState(fixture, new PerformanceObserver(checkpoint =>
        {
            if (checkpoint == "destination-state-committed") metrics.BaselineWrites++;
        }), metrics);
        var world = CreateBackupWorld(fixture.Source, fixture.Destination);
        var result = PerformancePipeline(fixture, state, metrics).Execute(world.GetAllEntities(), world);
        Assert.True(result.IsSuccess, result.ErrorMessage);
        var set = Assert.Single(Directory.GetDirectories(fixture.Destination, "*.backup"));
        var manifest = JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(Path.Combine(set, "manifest.json")))!;
        var archiveBytes = manifest.Parts.Sum(part => part.ArchiveBytes);
        output.WriteLine($"Archive bytes={archiveBytes}; publication checksum reads={metrics.PublicationBytes}; destination checksum reads={metrics.NasBytes}; staging checksum reads={metrics.StagingBytes}; baseline writes={metrics.BaselineWrites}.");
        Assert.True(metrics.StagingBytes == archiveBytes,
            $"Staging checksum reads: {metrics.StagingBytes} bytes for {archiveBytes} archive bytes; budget is one capture pass.");
        Assert.True(metrics.PublicationBytes == archiveBytes,
            $"Publication reread {metrics.PublicationBytes} bytes for {archiveBytes} archive bytes; budget is one postpublication commit pass.");
        Assert.True(metrics.NasBytes == 2 * archiveBytes,
            $"Fresh backup reread {metrics.NasBytes} NAS bytes for {archiveBytes} archive bytes; budget is transfer + postpublication commit.");
        Assert.Equal(1, metrics.BaselineWrites);
        AssertCorpusContents(fixture, set);
    }

    [Theory]
    [InlineData(64)]
    [InlineData(512)]
    public void FreshBackupLookupWorkStaysWithinLinearBudgets(int fileCount)
    {
        var fixture = PerformanceCorpus(fileCount);
        var metrics = new PerformanceMetrics(fixture.Destination);
        var world = CreateBackupWorld(fixture.Source, fixture.Destination);
        var counted = new PerformanceStorage(world);
        var result = PerformancePipeline(fixture, PerformanceState(fixture, null, metrics), metrics)
            .Execute(counted.GetAllEntities(), counted);
        Assert.True(result.IsSuccess, result.ErrorMessage);
        var parts = world.Query<BackupPartComponent>().Count;
        output.WriteLine($"Fresh backup: {fileCount} files, {parts} parts; file work={counted.FileWorkReads + counted.FileSnapshotEntries}; part reads={counted.PartWorkReads}; part snapshots={counted.PartSnapshotEntries}.");
        Assert.True(counted.FileWorkReads + counted.FileSnapshotEntries <= 32L * fileCount + 512,
            $"File lookup work: {counted.FileWorkReads} reads + {counted.FileSnapshotEntries} snapshot entries for {fileCount} files.");
        Assert.True(counted.PartWorkReads + counted.PartSnapshotEntries <= 32L * fileCount + 64L * parts + 512,
            $"Part lookup work: {counted.PartWorkReads} reads + {counted.PartSnapshotEntries} snapshot entries for {fileCount} files and {parts} parts.");
        Assert.True(counted.PartSnapshotEntries <= 32L * parts + 512,
            $"Part lookup work: {counted.PartSnapshotEntries} snapshot entries for {parts} parts.");
        AssertCorpusContents(fixture, Assert.Single(Directory.GetDirectories(fixture.Destination, "*.backup")));
    }

    [Theory]
    [InlineData(64, "part-transfer-committed")]
    [InlineData(512, "part-transfer-committed")]
    [InlineData(64, "publication-directory-renamed")]
    [InlineData(512, "publication-directory-renamed")]
    public void InterruptedRecoveryLookupWorkStaysWithinLinearBudgets(int fileCount, string checkpoint)
    {
        var fixture = PerformanceCorpus(fileCount);
        var first = PerformanceState(fixture, new ThrowAtCheckpointObserver(checkpoint), null);
        var initial = CreateBackupWorld(fixture.Source, fixture.Destination);
        Assert.False(PerformancePipeline(fixture, first, NullLogger.Instance).Execute(initial.GetAllEntities(), initial).IsSuccess);
        var resumed = CreateBackupWorld(fixture.Source, fixture.Destination);
        var existing = new Dictionary<string, Entity>(StringComparer.Ordinal);
        foreach (var path in Directory.GetFiles(fixture.Source))
        {
            var entity = new Entity();
            existing.Add(path, entity);
            resumed.SetComponent(entity, new FileWorkComponent
            {
                RunId = first.Job!.RunId, SourcePath = path,
                StableKey = Path.GetFileName(path), RelativePath = Path.GetFileName(path)
            });
        }
        var counted = new PerformanceStorage(resumed);
        var result = new BackupRecoverySystem(new(), new(), PerformanceState(fixture, null, null))
            .Execute(counted.GetAllEntities(), counted);
        Assert.True(result.IsSuccess, result.ErrorMessage);
        output.WriteLine($"Recovery at {checkpoint}: {fileCount} files; file work={counted.FileWorkReads + counted.FileSnapshotEntries}; queries={counted.FileQueries}.");
        Assert.True(counted.FileWorkReads + counted.FileSnapshotEntries <= 8L * fileCount + 128,
            $"Recovery did {counted.FileWorkReads} reads + {counted.FileSnapshotEntries} snapshot entries for {fileCount} files.");
        Assert.True(counted.FileQueries <= 3,
            $"Recovery queried the full file collection {counted.FileQueries} times.");
        var rows = resumed.Query<FileWorkComponent>();
        Assert.Equal(fileCount, rows.Count);
        foreach (var entity in rows)
        {
            var work = resumed.GetComponent<FileWorkComponent>(entity);
            Assert.Same(existing[work.SourcePath], entity);
        }
    }

    [Theory]
    [InlineData("part-transfer-committed")]
    [InlineData("published-job-committed")]
    public void SameSizeCorruptionAfterPublicationCannotCommitDestinationState(string boundary)
    {
        var fixture = PerformanceCorpus(8);
        var state = PerformanceState(fixture, new PerformanceObserver(checkpoint =>
        {
            if (checkpoint != boundary) return;
            var set = Assert.Single(Directory.GetDirectories(fixture.Destination, "*.backup*"));
            var path = Assert.Single(Directory.GetFiles(set, "*.zip"));
            var timestamp = File.GetLastWriteTimeUtc(path);
            var bytes = File.ReadAllBytes(path);
            bytes[bytes.Length / 2] ^= 1;
            File.WriteAllBytes(path, bytes);
            File.SetLastWriteTimeUtc(path, timestamp);
        }), null);
        var world = CreateBackupWorld(fixture.Source, fixture.Destination);
        var result = PerformancePipeline(fixture, state, NullLogger.Instance).Execute(world.GetAllEntities(), world);
        Assert.False(result.IsSuccess);
        Assert.False(File.Exists(state.DestinationState!.StatePath));
        Assert.True(state.Job!.Published);
        Assert.Equal(StateCommitStep.Pending, state.Job.StateCommitStep);
        Assert.True(Directory.Exists(state.StagingDirectory));
        var restart = CreateBackupWorld(fixture.Source, fixture.Destination);
        var recovered = PerformanceState(fixture, null, null);
        Assert.False(PerformancePipeline(fixture, recovered, NullLogger.Instance).Execute(restart.GetAllEntities(), restart).IsSuccess);
        Assert.False(File.Exists(state.DestinationState.StatePath));
        Assert.True(Directory.Exists(state.StagingDirectory));
    }

    [Theory]
    [InlineData(64, "publication-receipt-committed")]
    [InlineData(512, "publication-receipt-committed")]
    [InlineData(64, "publication-directory-renamed")]
    [InlineData(512, "publication-directory-renamed")]
    [InlineData(64, "published-job-committed")]
    [InlineData(512, "published-job-committed")]
    public void InterruptedPublicationReadsActualBytesAtRecoveryAndCommit(int fileCount, string boundary)
    {
        var fixture = PerformanceCorpus(fileCount, 8 * 1024);
        var firstMetrics = new PerformanceMetrics(fixture.Destination);
        var first = PerformanceState(fixture, new ThrowAtCheckpointObserver(boundary), firstMetrics);
        var initial = CreateBackupWorld(fixture.Source, fixture.Destination);
        Assert.False(PerformancePipeline(fixture, first, firstMetrics).Execute(initial.GetAllEntities(), initial).IsSuccess);
        var directory = Assert.Single(Directory.GetDirectories(fixture.Destination, "*.backup*"));
        var archiveBytes = Directory.GetFiles(directory, "*.zip").Sum(path => new FileInfo(path).Length);
        Assert.Equal(archiveBytes, firstMetrics.NasBytes);
        Assert.Equal(0, firstMetrics.PublicationBytes);
        var metrics = new PerformanceMetrics(fixture.Destination);
        var recovered = PerformanceState(fixture, null, metrics);
        var world = CreateBackupWorld(fixture.Source, fixture.Destination);
        // Recovery must use captured bytes even when source contents disappeared.
        foreach (var path in Directory.GetFiles(fixture.Source)) File.Delete(path);
        var result = PerformancePipeline(fixture, recovered, metrics).Execute(world.GetAllEntities(), world);
        Assert.True(result.IsSuccess, result.ErrorMessage);
        output.WriteLine($"Resumed {fileCount} files at {boundary}: archives={archiveBytes}; destination reads={metrics.NasBytes}; commit reads={metrics.PublicationBytes}.");
        Assert.Equal(2 * archiveBytes, metrics.NasBytes);
        Assert.Equal(archiveBytes, metrics.PublicationBytes);
        Assert.Equal(0, metrics.StagingBytes);
        Assert.Equal(StateCommitStep.Complete, recovered.Job!.StateCommitStep);
        Assert.Equal(fileCount, recovered.DestinationState!.LoadHashes().Count);
    }

    [Theory]
    [InlineData(64)]
    [InlineData(512)]
    public void BaselineAndIncrementalReadsOnlyContributingPartsOncePerBoundary(int fileCount)
    {
        var fixture = PerformanceCorpus(fileCount, 8 * 1024);
        var first = PerformanceState(fixture, null, null);
        var initial = CreateBackupWorld(fixture.Source, fixture.Destination);
        Assert.True(PerformancePipeline(fixture, first, NullLogger.Instance).Execute(initial.GetAllEntities(), initial).IsSuccess);
        var originalSet = Assert.Single(Directory.GetDirectories(fixture.Destination, "*.backup"));
        var originalParts = Directory.GetFiles(originalSet, "*.zip").Order(StringComparer.Ordinal).ToArray();
        var originalBytes = originalParts.Sum(path => new FileInfo(path).Length);
        var supersededBytes = new FileInfo(originalParts[0]).Length;
        first.ResetRun();

        var unchangedMetrics = new PerformanceMetrics(fixture.Destination);
        var unchanged = PerformanceState(fixture, new PerformanceObserver(checkpoint =>
        {
            if (checkpoint == "destination-state-committed") unchangedMetrics.BaselineWrites++;
        }), unchangedMetrics);
        var hashes = unchanged.DestinationState!.LoadHashes();
        Assert.Equal(fileCount, hashes.Count);
        Assert.Equal(originalBytes, unchangedMetrics.NasBytes);
        var world = CreateBackupWorld(fixture.Source, fixture.Destination);
        Assert.True(PerformancePipeline(fixture, unchanged, unchangedMetrics, hashes).Execute(world.GetAllEntities(), world).IsSuccess);
        Assert.Equal(originalBytes, unchangedMetrics.NasBytes);
        Assert.Equal(0, unchangedMetrics.StagingBytes);
        Assert.Equal(0, unchangedMetrics.PublicationBytes);
        Assert.Equal(0, unchangedMetrics.BaselineWrites);
        Assert.Single(Directory.GetDirectories(fixture.Destination, "*.backup"));
        unchanged.ResetRun();

        foreach (var path in Directory.GetFiles(fixture.Source).Order(StringComparer.Ordinal).Take(8))
            File.AppendAllText(path, "new incremental content");
        var incrementalMetrics = new PerformanceMetrics(fixture.Destination);
        var incremental = PerformanceState(fixture, null, incrementalMetrics);
        hashes = incremental.DestinationState!.LoadHashes();
        world = CreateBackupWorld(fixture.Source, fixture.Destination);
        var result = PerformancePipeline(fixture, incremental, incrementalMetrics, hashes).Execute(world.GetAllEntities(), world);
        Assert.True(result.IsSuccess, result.ErrorMessage);
        var delta = Directory.GetDirectories(fixture.Destination, "*.backup").Single(path => path != originalSet);
        var deltaManifest = JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(Path.Combine(delta, "manifest.json")))!;
        Assert.Equal(8, deltaManifest.FileCount);
        var deltaBytes = Assert.Single(deltaManifest.Parts).ArchiveBytes;
        Assert.Equal(originalBytes + 2 * deltaBytes, incrementalMetrics.NasBytes);
        Assert.Equal(deltaBytes, incrementalMetrics.PublicationBytes);

        var loadMetrics = new PerformanceMetrics(fixture.Destination);
        var store = PerformanceState(fixture, null, loadMetrics).DestinationState!;
        hashes = store.LoadHashes();
        var contributingBytes = originalBytes - supersededBytes + deltaBytes;
        output.WriteLine($"Baseline {fileCount} files: initial bytes={originalBytes}; superseded bytes={supersededBytes}; delta bytes={deltaBytes}; loaded bytes={loadMetrics.NasBytes}.");
        Assert.Equal(contributingBytes, loadMetrics.NasBytes);
        Assert.Equal(fileCount, hashes.Count);
        foreach (var path in Directory.GetFiles(fixture.Source))
            Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), hashes[path], ignoreCase: true);
        Assert.Equal(fileCount, store.LoadHashes().Count);
        Assert.Equal(2 * contributingBytes, loadMetrics.NasBytes); // No reuse across loads.
        var retained = originalParts[1];
        var timestamp = File.GetLastWriteTimeUtc(retained);
        var bytes = File.ReadAllBytes(retained);
        bytes[bytes.Length / 2] ^= 1;
        File.WriteAllBytes(retained, bytes);
        File.SetLastWriteTimeUtc(retained, timestamp);
        Assert.Throws<InvalidDataException>(() => store.LoadHashes());
    }

    private PerformancePaths PerformanceCorpus(int fileCount, int payloadBytes = 0)
    {
        var root = Path.Combine(_root, "performance-" + Guid.NewGuid().ToString("N"));
        var fixture = new PerformancePaths(Path.Combine(root, "source"), Path.Combine(root, "backup"), Path.Combine(root, "staging"));
        Directory.CreateDirectory(fixture.Source);
        Directory.CreateDirectory(fixture.Destination);
        var random = new Random(1729);
        for (var i = 0; i < fileCount; i++)
        {
            var bytes = payloadBytes == 0 ? Encoding.UTF8.GetBytes($"original content {i:0000}") : new byte[payloadBytes];
            if (payloadBytes > 0) random.NextBytes(bytes);
            File.WriteAllBytes(Path.Combine(fixture.Source, $"file-{i:0000}.mp3"), bytes);
        }
        return fixture;
    }

    private static BackupRunState PerformanceState(PerformancePaths paths, ICheckpointObserver? observer, PerformanceMetrics? metrics)
    {
        var state = new BackupRunState(paths.Source, paths.Destination, paths.Staging, observer)
        {
            DestinationState = new DestinationStateStore(paths.Source, paths.Destination)
            {
                ArtifactBytesReadObserver = metrics == null ? null : metrics.Read
            },
            ArtifactBytesReadObserver = metrics == null ? null : metrics.Read
        };
        return state;
    }

    private static DOPipeline.Pipeline.Pipeline PerformancePipeline(PerformancePaths paths, BackupRunState state, IPipelineLogger logger,
        ConcurrentDictionary<string, string>? hashes = null) =>
        BackupPipelineBuilder.BuildBackupPipeline(paths.Source, paths.Destination, hashes ?? new(), DestinationStateStore.ReadHistory(paths.Destination), logger, state,
            new BackupBatchOptions { MaxFilesPerPart = 8 },
            hashesPath: state.DestinationState!.StatePath, backupDatesPath: state.DestinationState.StatePath);

    private static void AssertCorpusContents(PerformancePaths paths, string set)
    {
        var expected = Directory.GetFiles(paths.Source).ToDictionary(path => Path.GetFileName(path)!, File.ReadAllBytes);
        var restored = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var path in Directory.GetFiles(set, "*.zip"))
        {
            using var archive = ZipFile.OpenRead(path);
            foreach (var entry in archive.Entries)
            {
                using var input = entry.Open();
                using var bytes = new MemoryStream();
                input.CopyTo(bytes);
                restored.Add(entry.FullName, bytes.ToArray());
            }
        }
        Assert.Equal(expected.Count, restored.Count);
        foreach (var pair in expected) Assert.Equal(pair.Value, restored[pair.Key!]);
    }

    private sealed record PerformancePaths(string Source, string Destination, string Staging);
    private sealed class PerformanceObserver(Action<string> observe) : ICheckpointObserver
    {
        public void Reached(string checkpoint) => observe(checkpoint);
    }

    private sealed class PerformanceMetrics(string destination) : IPipelineLogger
    {
        private long _stagingBytes;
        private long _nasBytes;
        private long _publicationBytes;
        private bool _publication;
        public long StagingBytes => _stagingBytes;
        public long NasBytes => _nasBytes;
        public long PublicationBytes => _publicationBytes;
        public int BaselineWrites;
        public void Read(string path, long bytes)
        {
            if (!Path.GetFileName(path).Contains(".zip", StringComparison.OrdinalIgnoreCase)) return;
            if (!path.StartsWith(DirectoryPath.WithSeparator(destination), StringComparison.Ordinal))
            {
                Interlocked.Add(ref _stagingBytes, bytes);
                return;
            }
            Interlocked.Add(ref _nasBytes, bytes);
            if (_publication) Interlocked.Add(ref _publicationBytes, bytes);
        }
        public void Log(string message)
        {
            if (message.Contains("Executing Pipe 6/6: 'Backup Publication'", StringComparison.Ordinal)) _publication = true;
        }
        public void Dispose() { }
    }

    private sealed class PerformanceStorage(IComponentStorage inner) : IComponentStorage
    {
        public long FileWorkReads;
        public long FileSnapshotEntries;
        public long PartWorkReads;
        public long PartSnapshotEntries;
        public int FileQueries;
        public T GetComponent<T>(Entity entity) where T : class, IComponent
        {
            if (typeof(T) == typeof(FileWorkComponent)) Interlocked.Increment(ref FileWorkReads);
            if (typeof(T) == typeof(BackupPartComponent)) Interlocked.Increment(ref PartWorkReads);
            return inner.GetComponent<T>(entity);
        }
        private IReadOnlyList<Entity> Count(IReadOnlyList<Entity> result, params Type[] types)
        {
            if (types.Contains(typeof(FileWorkComponent)))
            {
                Interlocked.Add(ref FileSnapshotEntries, result.Count);
                Interlocked.Increment(ref FileQueries);
            }
            if (types.Contains(typeof(BackupPartComponent))) Interlocked.Add(ref PartSnapshotEntries, result.Count);
            return result;
        }
        public void SetComponent<T>(Entity entity, T component) where T : class, IComponent => inner.SetComponent(entity, component);
        public bool HasComponent<T>(Entity entity) where T : class, IComponent => inner.HasComponent<T>(entity);
        public List<Entity> GetAllEntities() => inner.GetAllEntities();
        public IReadOnlyList<Entity> Query<T>() where T : class, IComponent => Count(inner.Query<T>(), typeof(T));
        public IReadOnlyList<Entity> Query<T1, T2>() where T1 : class, IComponent where T2 : class, IComponent =>
            Count(inner.Query<T1, T2>(), typeof(T1), typeof(T2));
        public IReadOnlyList<Entity> Query<T1, T2, T3>() where T1 : class, IComponent where T2 : class, IComponent where T3 : class, IComponent =>
            Count(inner.Query<T1, T2, T3>(), typeof(T1), typeof(T2), typeof(T3));
    }
}
