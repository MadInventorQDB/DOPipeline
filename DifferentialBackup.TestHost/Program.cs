using System.Collections.Concurrent;
using DifferentialBackup.Components;
using DifferentialBackup.Pipeline;
using DifferentialBackup.Systems;
using DifferentialBackup.Utilities;
using DOPipeline.Entities;
using DOPipeline.Logging;
using DOPipeline.Storage;
using DOPipeline.Utilities;

if (args.Length >= 4 && args[0].Equals("restore", StringComparison.OrdinalIgnoreCase))
{
    return RunRestore(args);
}

if (args.Length < 5 || !args[0].Equals("backup", StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine("backup <source> <destination> <staging> <hashes> <dates> [checkpoint] [signal] [release]");
    return 2;
}

return RunBackup(args);

static int RunRestore(string[] args)
{
    var backupDestination = Path.GetFullPath(args[1]);
    var restoreDestination = Path.GetFullPath(args[2]);
    var dateString = args[3];
    if (!DateTime.TryParseExact(
        dateString,
        "yyyyMMddHHmmss",
        System.Globalization.CultureInfo.InvariantCulture,
        System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
        out var requestedBackupDate))
    {
        Console.Error.WriteLine("Invalid backup date format. Expected yyyyMMddHHmmss.");
        return 2;
    }

    var checkpoint = args.Length > 4 ? args[4] : string.Empty;
    var signalPath = args.Length > 5 ? Path.GetFullPath(args[5]) : string.Empty;
    var releasePath = args.Length > 6 ? Path.GetFullPath(args[6]) : string.Empty;

    using var restoreBackupLock = ExclusiveOperationLock.Acquire(
        Path.Combine(backupDestination, ".differential-backup.lock"));
    using var restoreDestinationLock = ExclusiveOperationLock.Acquire(
        Path.Combine(restoreDestination, ".differential-restore.lock"));
    using var cancellation = new CancellationTokenSource();
    var observer = new BlockingCheckpointObserver(checkpoint, signalPath, releasePath, null, cancellation, "");

    var storage = new ComponentStorage();
    var operation = new Entity();
    var runId = Guid.NewGuid();
    storage.SetComponent(operation, new OperationComponent
    {
        RunId = runId,
        OperationType = "restore",
        BackupDestination = backupDestination,
        RestoreDestination = restoreDestination,
        BackupDate = requestedBackupDate,
        Phase = OperationPhase.InitialPass,
        ActivePass = 0,
        InitializationSucceeded = true
    });
    storage.SetComponent(operation, new RetryScheduleComponent { RunId = runId });

    var clock = new FixtureClock();
    var pipeline = RestorePipelineBuilder.BuildRestorePipeline(
        backupDestination,
        requestedBackupDate,
        restoreDestination,
        NullLogger.Instance,
        observer,
        clock);

    for (var pass = 0; pass < 20; pass++)
    {
        var result = pipeline.Execute(new[] { operation }, storage, cancellation.Token);
        if (!result.IsSuccess)
        {
            Console.Error.WriteLine(result.ErrorMessage);
            return result.IsCancellation ? 130 : 2;
        }

        var op = storage.GetComponent<OperationComponent>(operation);
        var waitInstruction = storage.GetComponent<OperationWaitComponent>(operation);
        if (op?.Phase == OperationPhase.RetryWaiting && waitInstruction != null)
        {
            clock.Now = waitInstruction.WakeAtUtc;
            continue;
        }
        if (op?.Phase == OperationPhase.Finished) break;
    }
    var outcome = storage.GetComponent<OperationOutcomeComponent>(operation);
    return outcome?.ExitCode ?? 0;
}

static int RunBackup(string[] args)
{
    var source = Path.GetFullPath(args[1]);
    var destination = Path.GetFullPath(args[2]);
    var staging = Path.GetFullPath(args[3]);
    var hashesPath = Path.GetFullPath(args[4]);
    var datesPath = Path.GetFullPath(args.Length > 5 ? args[5] : Path.Combine(Path.GetDirectoryName(hashesPath)!, "dates.json"));
    var checkpoint = args.Length > 6 ? args[6] : string.Empty;
    var signalPath = args.Length > 7 ? Path.GetFullPath(args[7]) : string.Empty;
    var releasePath = args.Length > 8 ? Path.GetFullPath(args[8]) : string.Empty;

    using var stateLock = ExclusiveOperationLock.Acquire(hashesPath + ".lock");
    using var destinationLock = ExclusiveOperationLock.Acquire(Path.Combine(destination, ".backup.lock"));
    using var cancellation = new CancellationTokenSource();
    var observer = new BlockingCheckpointObserver(checkpoint, signalPath, releasePath,
        args.Length > 9 ? args[9] : null, cancellation, args.Length > 10 ? args[10] : "");
    var clock = new FixtureClock();
    var runState = new BackupRunState(source, destination, staging, observer);
    var hashes = DataPersistence.LoadFileHashes(hashesPath);
    var dates = DataPersistence.LoadBackupDates(datesPath);
    var storage = new ComponentStorage();
    var operation = new Entity();
    var runId = Guid.NewGuid();
    storage.SetComponent(operation, new OperationComponent
    {
        RunId = runId,
        OperationType = "backup",
        SourceDirectory = source,
        BackupDestination = destination,
        Phase = OperationPhase.InitialPass,
        ActivePass = 0,
        InitializationSucceeded = true
    });
    storage.SetComponent(operation, new RetryScheduleComponent { RunId = runId });

    var pipeline = BackupPipelineBuilder.BuildBackupPipeline(
        source,
        destination,
        hashes,
        dates,
        NullLogger.Instance,
        runState,
        new BackupBatchOptions { MaxFilesPerPart = 1, MaxSourceBytesPerPart = 1024, TransferQueueCapacity = 1 },
        hashesPath: hashesPath,
        backupDatesPath: datesPath,
        clock: clock);
    for (var pass = 0; pass < 20; pass++)
    {
        var result = pipeline.Execute(new[] { operation }, storage, cancellation.Token);
        if (!result.IsSuccess)
        {
            Console.Error.WriteLine(result.ErrorMessage);
            if (result.IsCancellation)
                storage.SetComponent(operation, new CancellationSignalComponent
                {
                    RunId = storage.GetComponent<OperationComponent>(operation)!.RunId,
                    Reason = result.ErrorMessage ?? "Cancelled"
                });
            else storage.SetComponent(operation, new FatalFailureComponent
            {
                RunId = storage.GetComponent<OperationComponent>(operation)!.RunId,
                Message = result.ErrorMessage ?? "Pipeline failed"
            });
            new OperationSummarySystem().Execute(new[] { operation }, storage);
            return storage.GetComponent<OperationOutcomeComponent>(operation)!.ExitCode;
        }
        var outcome = storage.GetComponent<OperationOutcomeComponent>(operation);
        if (outcome != null) return outcome.ExitCode;
        if (storage.GetComponent<OperationWaitComponent>(operation) is { } wait)
            clock.Now = wait.WakeAtUtc;
    }
    throw new InvalidOperationException("Fixture operation did not terminate.");
}

sealed class FixtureClock : IClock
{
    public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UtcNow => Now;
}

sealed class NullLogger : IPipelineLogger
{
    public static readonly NullLogger Instance = new();
    public void Log(string message) { }
    public void Dispose() { }
}

sealed class BlockingCheckpointObserver : ICheckpointObserver
{
    private readonly string _checkpoint;
    private readonly string _signalPath;
    private readonly string _releasePath;
    private readonly string? _omitPath;
    private readonly CancellationTokenSource _cancellation;
    private readonly string _mode;

    public BlockingCheckpointObserver(string checkpoint, string signalPath, string releasePath, string? omitPath,
        CancellationTokenSource cancellation, string mode)
    {
        _checkpoint = checkpoint;
        _signalPath = signalPath;
        _releasePath = releasePath;
        _omitPath = omitPath;
        _cancellation = cancellation;
        _mode = mode;
    }

    public void Reached(string checkpoint)
    {
        if (checkpoint == "producer-part-starting" && _mode.Length > 0)
        {
            _cancellation.Cancel();
            if (_mode == "fatal-and-cancel") throw new IOException("initiating fixture I/O failure");
            _cancellation.Token.ThrowIfCancellationRequested();
        }
        if (checkpoint == "part-plan-committed" && !string.IsNullOrEmpty(_omitPath))
            File.Delete(_omitPath);
        if (string.IsNullOrWhiteSpace(_checkpoint) ||
            !string.Equals(_checkpoint, checkpoint, StringComparison.Ordinal))
        {
            return;
        }

        Console.WriteLine("CHECKPOINT:" + checkpoint);
        Console.Out.Flush();

        if (!string.IsNullOrWhiteSpace(_signalPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_signalPath)!);
            using (var stream = new FileStream(
                _signalPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.Read))
            using (var writer = new StreamWriter(stream))
            {
                writer.WriteLine(checkpoint);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
        }

        if (!string.IsNullOrWhiteSpace(_releasePath))
        {
            var releaseDirectory = Path.GetDirectoryName(_releasePath)!;
            var releaseName = Path.GetFileName(_releasePath);
            using var released = new ManualResetEventSlim(File.Exists(_releasePath));
            using var watcher = new FileSystemWatcher(releaseDirectory, releaseName)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                EnableRaisingEvents = true
            };
            FileSystemEventHandler signal = (_, _) => released.Set();
            RenamedEventHandler renamed = (_, _) => released.Set();
            watcher.Created += signal;
            watcher.Changed += signal;
            watcher.Renamed += renamed;
            if (File.Exists(_releasePath)) released.Set();
            if (!released.IsSet && !released.Wait(TimeSpan.FromMinutes(30)))
            {
                throw new TimeoutException($"Timed out waiting for checkpoint release '{_releasePath}'.");
            }
        }
    }
}
