using DifferentialBackup.Components;
using DifferentialBackup.Pipeline;
using DifferentialBackup.Systems;
using DifferentialBackup.Utilities;
using DOPipeline.Entities;
using DOPipeline.Logging; // <-- Added using
using DOPipeline.Storage;
using DOPipeline.Utilities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace DifferentialBackup
{
    internal class Program
    {
        // Define logger variable accessible by static methods
        // Marked as nullable because it's initialized in Main
        private static IPipelineLogger? _pipelineLogger;

        // Enum for command-line operations
        enum Operation
        {
            Backup,
            Restore,
            Query,
            Duplicate,
            Help
        }

        static void Main(string[] args)
        {
            // --- Initialize Logger ---
            string logFilePath = Path.Combine(AppContext.BaseDirectory, "differential_backup_pipeline.log");
            // Ensure logger is assigned to the static field
            _pipelineLogger = new SafePipelineLogger(new FileConsoleLogger(logFilePath));
            // --- Logger Initialized ---

            // Wrap the main logic in a try-finally to ensure logger disposal
            try
            {
                if (args.Length == 0)
                {
                    DisplayHelp();
                    return; // Exit after displaying help
                }

                // Parse the operation
                if (!Enum.TryParse(args[0], true, out Operation operation))
                {
                    _pipelineLogger.Log($"[ERROR] Invalid operation specified: {args[0]}");
                    Console.WriteLine($"Invalid operation: {args[0]}");
                    DisplayHelp();
                    Environment.ExitCode = 2;
                    return; // Exit after error
                }

                _pipelineLogger.Log($"Operation '{operation}' starting...");

                // Execute the chosen operation
                switch (operation)
                {
                    case Operation.Backup:
                        PerformBackup(args.Skip(1).ToArray());
                        break;
                    case Operation.Restore:
                        PerformRestore(args.Skip(1).ToArray());
                        break;
                    case Operation.Query:
                        PerformQuery(args.Skip(1).ToArray());
                        break;
                    case Operation.Duplicate:
                        PerformDuplicateDetection(args.Skip(1).ToArray());
                        break;
                    case Operation.Help:
                    default:
                        DisplayHelp();
                        break;
                }

                _pipelineLogger.Log($"Operation '{operation}' finished.");
            }
            catch (Exception ex) // Catch unexpected exceptions during operation execution
            {
                string errorMessage = $"[FATAL] An unexpected error occurred during operation: {ex.Message}\nStack Trace:\n{ex.StackTrace}";
                // Use null-conditional operator just in case logger wasn't initialized somehow
                SafeLog(errorMessage);
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("\nAn unexpected error occurred. Check the log file for details.");
                Console.WriteLine($"Error: {ex.Message}");
                Console.ResetColor();
                // A thrown OCE is not evidence of user cancellation. The
                // operation paths classify cancellation using their token and
                // return 130 explicitly; an unrelated exception remains fatal.
                Environment.ExitCode = 2;
            }
            finally
            {
                // --- Dispose Logger ---
                // Ensure disposal happens even if errors occurred
                try
                {
                    _pipelineLogger?.Dispose();
                }
                catch
                {
                    // Logger cleanup cannot replace the operation result.
                }
                // ----------------------
            }
        }

        // --- Backup Operation ---
        static void PerformBackup(string[] args)
        {
            // Ensure logger is available (should be initialized in Main)
            if (_pipelineLogger == null)
            {
                Console.WriteLine("[FATAL ERROR] Logger not initialized. Cannot proceed.");
                return;
            }

            _pipelineLogger.Log("Starting Backup operation setup.");

            string sourceDirectory;
            string backupDestination;

            // Determine source and destination from args or prompt user
            if (args.Length >= 2)
            {
                sourceDirectory = args[0];
                backupDestination = args[1];
                _pipelineLogger.Log($"Using command-line arguments for Backup: Source='{sourceDirectory}', Destination='{backupDestination}'");
                Console.WriteLine("Using command-line arguments for Backup:");
                Console.WriteLine($"Source Directory: {sourceDirectory}");
                Console.WriteLine($"Backup Destination: {backupDestination}");
            }
            else
            {
                _pipelineLogger.Log("Insufficient command-line arguments. Prompting user for Backup parameters.");
                Console.WriteLine("Backup operation requires Source Directory and Backup Destination.");

                // Prompt for Source Directory
                sourceDirectory = PromptForDirectory("Enter the Source Directory", mustExist: true);
                if (string.IsNullOrEmpty(sourceDirectory)) { Environment.ExitCode = 2; return; }

                // Prompt for Backup Destination
                backupDestination = PromptForDirectory("Enter the Backup Destination", mustExist: false);
                if (string.IsNullOrEmpty(backupDestination)) { Environment.ExitCode = 2; return; }
                _pipelineLogger.Log($"User provided Backup parameters: Source='{sourceDirectory}', Destination='{backupDestination}'");
            }

            // Validate directories after getting them
            if (!Directory.Exists(sourceDirectory))
            {
                _pipelineLogger.Log($"[ERROR] Source directory does not exist: '{sourceDirectory}'");
                Console.WriteLine($"Error: Source directory not found: {sourceDirectory}");
                Environment.ExitCode = 2;
                return;
            }
            var normalizedSource = Path.GetFullPath(sourceDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var normalizedDestination = Path.GetFullPath(backupDestination)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (PathsOverlap(normalizedSource, normalizedDestination))
            {
                _pipelineLogger.Log("[ERROR] Source and backup destinations overlap.");
                Console.WriteLine("Error: The backup destination must not be inside or contain the source directory.");
                Environment.ExitCode = 2;
                return;
            }
            try // Ensure backup destination can be created/accessed
            {
                Directory.CreateDirectory(backupDestination);
            }
            catch (Exception ex)
            {
                _pipelineLogger.Log($"[ERROR] Cannot create or access backup destination '{backupDestination}': {ex.Message}");
                Console.WriteLine($"Error accessing backup destination: {ex.Message}");
                Environment.ExitCode = 2;
                return;
            }

            ExclusiveOperationLock? stateLock = null;
            ExclusiveOperationLock? destinationLock = null;
            try
            {
                stateLock = ExclusiveOperationLock.Acquire(
                    Path.Combine(AppContext.BaseDirectory, "differential-backup.state.lock"));
                destinationLock = ExclusiveOperationLock.Acquire(
                    Path.Combine(backupDestination, ".differential-backup.lock"));
            }
            catch (Exception ex)
            {
                stateLock?.Dispose();
                destinationLock?.Dispose();
                _pipelineLogger.Log($"[ERROR] Could not acquire backup locks: {ex.Message}");
                Console.WriteLine($"Backup failed: {ex.Message}");
                Environment.ExitCode = 2;
                return;
            }

            using (stateLock)
            using (destinationLock)
            {


            _pipelineLogger.Log("Initializing storage and loading persisted data.");
            // Initialize storage and load persisted data
            var storage = new ComponentStorage();

            // File paths for persisted data
            var hashesFilePath = Path.Combine(AppContext.BaseDirectory, "fileHashes.json");
            var backupDatesFilePath = Path.Combine(AppContext.BaseDirectory, "backupDates.json");

            // Load persisted data
            _pipelineLogger.Log($"Loading file hashes from '{hashesFilePath}'.");
            var fileHashes = DataPersistence.LoadFileHashes(hashesFilePath);
            _pipelineLogger.Log($"Loading backup dates from '{backupDatesFilePath}'.");
            var backupDates = DataPersistence.LoadBackupDates(backupDatesFilePath);
            _pipelineLogger.Log($"Loaded {fileHashes.Count} file hashes and {backupDates.Count} backup dates.");
            var backupRunState = new BackupRunState(sourceDirectory, backupDestination);

            // The initial entity acts as a starting point for the FileDiscoverySystem
            var initialEntity = new Entity();
            var entities = new List<Entity> { initialEntity };
            var runId = Guid.NewGuid();
            storage.SetComponent(initialEntity, new OperationComponent
            {
                RunId = runId,
                OperationType = "backup",
                SourceDirectory = Path.GetFullPath(sourceDirectory),
                BackupDestination = Path.GetFullPath(backupDestination),
                Phase = OperationPhase.InitialPass,
                ActivePass = 0,
                InitializationSucceeded = true
            });
            storage.SetComponent(initialEntity, new RetryScheduleComponent { RunId = runId });

            var backupDateCountBeforeRun = backupDates.Count;

            using var cancellation = new CancellationTokenSource();
            ConsoleCancelEventHandler? cancelHandler = null;
            cancelHandler = (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cancellation.Cancel();
            };
            Console.CancelKeyPress += cancelHandler;

            _pipelineLogger.Log("Building Backup pipeline...");
            // Build the backup pipeline
            var backupPipeline = BackupPipelineBuilder.BuildBackupPipeline(
                sourceDirectory,
                backupDestination,
                fileHashes,
                backupDates,
                _pipelineLogger,
                backupRunState,
                hashesPath: hashesFilePath,
                backupDatesPath: backupDatesFilePath);

            _pipelineLogger.Log("Executing Backup pipeline...");
            // Execute the pipeline
            var backupResult = backupPipeline.Execute(entities, storage, cancellation.Token);

            while (backupResult.IsSuccess)
            {
                var operation = storage.GetComponent<OperationComponent>(initialEntity);
                var schedule = storage.GetComponent<RetryScheduleComponent>(initialEntity);
                var waitInstruction = storage.GetComponent<OperationWaitComponent>(initialEntity);
                if (operation == null || operation.Phase != OperationPhase.RetryWaiting ||
                    schedule?.NextAttemptUtc == null || waitInstruction == null)
                {
                    break;
                }

                var wait = waitInstruction.WakeAtUtc - DateTimeOffset.UtcNow;
                if (wait > TimeSpan.Zero)
                {
                    _pipelineLogger.Log($"Retry round {schedule.ActiveRound} scheduled for {schedule.NextAttemptUtc.Value:O}.");
                    try
                    {
                        Task.Delay(wait, cancellation.Token).GetAwaiter().GetResult();
                    }
                    catch (OperationCanceledException)
                    {
                        cancellation.Cancel();
                        var currentRunId = storage.GetComponent<OperationComponent>(initialEntity)?.RunId ?? runId;
                        storage.SetComponent(initialEntity, new CancellationSignalComponent
                        {
                            RunId = currentRunId,
                            Reason = "Operation cancelled while waiting for retry."
                        });
                        backupResult = Result.Cancelled("Operation cancelled while waiting for retry.");
                        break;
                    }
                }

                // RetryActivationSystem owns the transition from the durable
                // wait instruction to the active pass. The host only honors
                // the requested wake time and re-enters the pipeline.
                backupResult = backupPipeline.Execute(entities, storage, cancellation.Token);
            }

            if (!backupResult.IsSuccess)
            {
                var operation = storage.GetComponent<OperationComponent>(initialEntity);
                var currentRunId = operation?.RunId ?? runId;
                if (backupResult.IsCancellation)
                {
                    storage.SetComponent(initialEntity, new CancellationSignalComponent
                    {
                        RunId = currentRunId,
                        Reason = backupResult.ErrorMessage ?? "Operation cancelled."
                    });
                }
                else
                {
                    storage.SetComponent(initialEntity, new FatalFailureComponent
                    {
                        RunId = currentRunId,
                        Stage = "Pipeline",
                        Message = backupResult.ErrorMessage ?? "Backup pipeline failed.",
                        ExceptionType = backupResult.Exception?.GetType().FullName
                    });
                }
                var failedOperation = storage.GetComponent<OperationComponent>(initialEntity);
                if (failedOperation != null)
                {
                    // Keep the last durable/recoverable phase. Summary is
                    // allowed to produce a failed/cancelled report from a
                    // fatal signal without erasing the recovery checkpoint.
                    _ = new OperationSummarySystem().Execute(entities, storage);
                }
                SafeLog($"[ERROR] Backup did not complete: {backupResult.ErrorMessage}");
                Console.WriteLine($"Backup did not complete: {backupResult.ErrorMessage}");
                Console.WriteLine("Completed parts and local checkpoints will be reused by the next backup run.");
                Environment.ExitCode = backupResult.IsCancellation ? 130 : 2;
                Console.CancelKeyPress -= cancelHandler;
                return;
            }

            var outcome = storage.GetComponent<OperationOutcomeComponent>(initialEntity);
            if (outcome?.Outcome == OperationOutcome.Failed || outcome?.Outcome == OperationOutcome.Cancelled)
            {
                Environment.ExitCode = outcome.ExitCode;
                Console.CancelKeyPress -= cancelHandler;
                return;
            }

            // Publication adds a date only after every part and the manifest are durable.
            bool newBackupOccurred = backupDates.Count > backupDateCountBeforeRun;

            backupRunState.ClearRun();
            _pipelineLogger.Log("Backup operation complete.");
            if (outcome?.Outcome is OperationOutcome.IncompleteBackup or OperationOutcome.Warnings)
            {
                _pipelineLogger.Log("Backup completed with warnings; unresolved source paths remain in the report.");
                Console.WriteLine("Backup completed with warnings. Some source paths could not be captured.");
            }
            else if (newBackupOccurred)
            {
                _pipelineLogger.Log("Backup completed. New backup version created.");
                Console.WriteLine("Backup completed successfully.");
            }
            else
            {
                _pipelineLogger.Log("Backup process finished. No changes detected or no files needed backup.");
                Console.WriteLine("Backup finished. No changes detected.");
            }
            Environment.ExitCode = outcome?.ExitCode ?? 0;
            Console.CancelKeyPress -= cancelHandler;
            }
        }

        private static bool PathsOverlap(string left, string right)
        {
            static string WithSeparator(string value) => value + Path.DirectorySeparatorChar;
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase) ||
                WithSeparator(left).StartsWith(WithSeparator(right), StringComparison.OrdinalIgnoreCase) ||
                WithSeparator(right).StartsWith(WithSeparator(left), StringComparison.OrdinalIgnoreCase);
        }

        // --- Restore Operation ---
        static void PerformRestore(string[] args)
        {
            if (_pipelineLogger == null) { Console.WriteLine("[FATAL ERROR] Logger not initialized. Cannot proceed."); Environment.ExitCode = 2; return; }
            _pipelineLogger.Log("Starting Restore operation setup.");

            string backupDestination;
            string restoreDestination;
            DateTime? requestedBackupDate = null;

            // Get parameters
            if (args.Length >= 2)
            {
                backupDestination = args[0];
                restoreDestination = args[1];
                for (var index = 2; index < args.Length; index++)
                {
                    if (!string.Equals(args[index], "--backup-date", StringComparison.OrdinalIgnoreCase) ||
                        index + 1 >= args.Length ||
                        !DateTime.TryParseExact(
                            args[++index],
                            "yyyyMMddHHmmss",
                            System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                            out var parsedDate))
                    {
                        Console.WriteLine("Invalid restore option. Use --backup-date yyyyMMddHHmmss.");
                        Environment.ExitCode = 2;
                        return;
                    }

                    requestedBackupDate = parsedDate;
                }
                _pipelineLogger.Log($"Using command-line arguments for Restore: BackupDest='{backupDestination}', RestoreDest='{restoreDestination}'");
                Console.WriteLine("Using command-line arguments for Restore:");
                Console.WriteLine($"Backup Destination: {backupDestination}");
                Console.WriteLine($"Restore Destination: {restoreDestination}");
            }
            else
            {
                _pipelineLogger.Log("Insufficient command-line arguments. Prompting user for Restore parameters.");
                Console.WriteLine("Restore operation requires Backup Destination and Restore Destination.");

                backupDestination = PromptForDirectory("Enter the Backup Destination", mustExist: true);
                if (string.IsNullOrEmpty(backupDestination)) { Environment.ExitCode = 2; return; }

                restoreDestination = PromptForDirectory("Enter the Restore Destination", mustExist: false);
                if (string.IsNullOrEmpty(restoreDestination)) { Environment.ExitCode = 2; return; }
                _pipelineLogger.Log($"User provided Restore parameters: BackupDest='{backupDestination}', RestoreDest='{restoreDestination}'");
            }

            // Validate directories
            if (!Directory.Exists(backupDestination))
            {
                _pipelineLogger.Log($"[ERROR] Backup destination directory does not exist: '{backupDestination}'");
                Console.WriteLine($"Error: Backup destination not found: {backupDestination}");
                Environment.ExitCode = 2;
                return;
            }
            try
            {
                Directory.CreateDirectory(restoreDestination);
            }
            catch (Exception ex)
            {
                _pipelineLogger.Log($"[ERROR] Cannot create or access restore destination '{restoreDestination}': {ex.Message}");
                Console.WriteLine($"Error accessing restore destination: {ex.Message}");
                Environment.ExitCode = 2;
                return;
            }

            using var restoreStateLock = ExclusiveOperationLock.Acquire(
                Path.Combine(AppContext.BaseDirectory, "differential-backup.state.lock"));
            using var restoreBackupLock = ExclusiveOperationLock.Acquire(
                Path.Combine(backupDestination, ".differential-backup.lock"));
            using var restoreDestinationLock = ExclusiveOperationLock.Acquire(
                Path.Combine(restoreDestination, ".differential-restore.lock"));
            using var restoreCancellation = new CancellationTokenSource();
            ConsoleCancelEventHandler restoreCancelHandler = (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                restoreCancellation.Cancel();
            };
            Console.CancelKeyPress += restoreCancelHandler;


            _pipelineLogger.Log("Initializing storage and loading backup dates data for Restore.");
            // Initialize storage and load backup dates
            var storage = new ComponentStorage();
            var backupDatesFilePath = Path.Combine(AppContext.BaseDirectory, "backupDates.json");
            _pipelineLogger.Log($"Loading backup dates from '{backupDatesFilePath}'.");
            var backupDates = DataPersistence.LoadBackupDates(backupDatesFilePath);
            _pipelineLogger.Log($"Loaded {backupDates.Count} backup dates.");

            if (!backupDates.Any() && requestedBackupDate == null)
            {
                _pipelineLogger.Log("No backup dates found in persisted data. Cannot perform restore.");
                Console.WriteLine("No backup history found. Cannot restore.");
                Environment.ExitCode = 2;
                Console.CancelKeyPress -= restoreCancelHandler;
                return;
            }

            // We use the Query pipeline first to present available dates or find the latest
            var initialEntity = new Entity();
            // The QueryBackupDatesSystem doesn't strictly need this, but consistency is okay.
            storage.SetComponent(initialEntity, new FilePathComponent { FilePath = backupDestination });
            var runId = Guid.NewGuid();
            storage.SetComponent(initialEntity, new OperationComponent
            {
                RunId = runId,
                OperationType = "restore",
                BackupDestination = Path.GetFullPath(backupDestination),
                RestoreDestination = Path.GetFullPath(restoreDestination),
                Phase = OperationPhase.InitialPass,
                InitializationSucceeded = true
            });
            storage.SetComponent(initialEntity, new RetryScheduleComponent { RunId = runId });
            var entities = new List<Entity> { initialEntity };

            _pipelineLogger.Log("Building QueryBackupDates pipeline to find available dates for Restore...");
            var queryPipeline = QueryBackupDatesPipelineBuilder.BuildQueryBackupDatesPipeline(
                backupDates,
                _pipelineLogger);

            _pipelineLogger.Log("Executing QueryBackupDates pipeline for Restore...");
            var queryResult = queryPipeline.Execute(entities, storage, restoreCancellation.Token);

            if (queryResult.IsSuccess)
            {
                var backupDatesComponent = storage.GetComponent<BackupDatesComponent>(initialEntity);
                if (requestedBackupDate != null || backupDatesComponent != null && backupDatesComponent.Dates.Any())
                {
                    var latestBackupDate = requestedBackupDate ?? backupDatesComponent!.Dates.First();
                    _pipelineLogger.Log($"Identified latest backup date for restore: {latestBackupDate:yyyy-MM-dd HH:mm:ss}");

                    _pipelineLogger.Log("Building Restore pipeline...");
                    var restorePipeline = RestorePipelineBuilder.BuildRestorePipeline(
                        backupDestination,
                        latestBackupDate,
                        restoreDestination,
                        _pipelineLogger);

                    _pipelineLogger.Log("Executing Restore pipeline...");
                    var restoreResult = restorePipeline.Execute(entities, storage, restoreCancellation.Token); // RestoreSystem operates based on constructor args

                    while (restoreResult.IsSuccess)
                    {
                        var operation = storage.GetComponent<OperationComponent>(initialEntity);
                        var schedule = storage.GetComponent<RetryScheduleComponent>(initialEntity);
                        var waitInstruction = storage.GetComponent<OperationWaitComponent>(initialEntity);
                        if (operation == null || operation.Phase != OperationPhase.RetryWaiting ||
                            schedule?.NextAttemptUtc == null || waitInstruction == null)
                        {
                            break;
                        }

                        var wait = waitInstruction.WakeAtUtc - DateTimeOffset.UtcNow;
                        if (wait > TimeSpan.Zero)
                        {
                            _pipelineLogger.Log($"Restore retry round {schedule.ActiveRound} scheduled for {schedule.NextAttemptUtc.Value:O}.");
                            try
                            {
                                Task.Delay(wait, restoreCancellation.Token).GetAwaiter().GetResult();
                            }
                            catch (OperationCanceledException)
                            {
                                restoreResult = Result.Cancelled("Restore cancelled while waiting for retry.");
                                break;
                            }
                        }

                        restoreResult = restorePipeline.Execute(entities, storage, restoreCancellation.Token);
                    }

                    if (restoreResult.IsSuccess)
                    {
                        var outcome = storage.GetComponent<OperationOutcomeComponent>(initialEntity);
                        if (outcome?.Outcome == OperationOutcome.Warnings)
                        {
                            _pipelineLogger.Log("Restore completed with warnings; unresolved entries remain retryable.");
                            Console.WriteLine("Restore completed with warnings. Some entries could not be restored.");
                            Environment.ExitCode = 1;
                        }
                        else
                        {
                            _pipelineLogger.Log("Restore pipeline completed successfully.");
                            Console.WriteLine("Restore completed successfully.");
                            Environment.ExitCode = 0;
                        }
                    }
                    else
                    {
                        // RestoreSystem provides specific error messages on failure
                        if (restoreResult.IsCancellation)
                        {
                            storage.SetComponent(initialEntity, new CancellationSignalComponent
                            {
                                RunId = runId,
                                Reason = restoreResult.ErrorMessage ?? "Restore cancelled."
                            });
                        }
                        else
                        {
                            storage.SetComponent(initialEntity, new FatalFailureComponent
                            {
                                RunId = runId,
                                Stage = "Restore",
                                Message = restoreResult.ErrorMessage ?? "Restore failed.",
                                ExceptionType = restoreResult.Exception?.GetType().FullName
                            });
                        }
                        var failedOperation = storage.GetComponent<OperationComponent>(initialEntity);
                        if (failedOperation != null)
                        {
                            failedOperation.Phase = OperationPhase.Finalizing;
                            storage.SetComponent(initialEntity, failedOperation);
                            _ = new OperationSummarySystem().Execute(entities, storage);
                        }
                        _pipelineLogger.Log($"[ERROR] Restore pipeline failed: {restoreResult.ErrorMessage}");
                        Console.WriteLine($"Restore failed: {restoreResult.ErrorMessage}");
                        Environment.ExitCode = restoreResult.IsCancellation ? 130 : 2;
                    }
                }
                else
                {
                    _pipelineLogger.Log("Query completed, but no backup dates were found in the component (this shouldn't happen if initial load succeeded).");
                    Console.WriteLine("No backup dates found to restore from.");
                    Environment.ExitCode = 2;
                }
            }
            else
            {
                // This indicates a failure within the Query pipeline itself.
                _pipelineLogger.Log($"[ERROR] Failed to query backup dates: {queryResult.ErrorMessage}");
                Console.WriteLine($"Failed to query backup dates: {queryResult.ErrorMessage}");
                Environment.ExitCode = 2;
            }
            _pipelineLogger.Log("Restore operation complete.");
            Console.CancelKeyPress -= restoreCancelHandler;
        }

        // --- Query Operation ---
        static void PerformQuery(string[] args)
        {
            if (_pipelineLogger == null) { Console.WriteLine("[FATAL ERROR] Logger not initialized. Cannot proceed."); return; }
            _pipelineLogger.Log("Starting Query operation setup.");

            string backupDestination;

            if (args.Length >= 1)
            {
                backupDestination = args[0];
                _pipelineLogger.Log($"Using command-line argument for Query: BackupDest='{backupDestination}'");
                Console.WriteLine("Using command-line argument for Query:");
                Console.WriteLine($"Backup Destination: {backupDestination}");
            }
            else
            {
                _pipelineLogger.Log("Insufficient command-line arguments. Prompting user for Query parameters.");
                Console.WriteLine("Query operation requires Backup Destination.");
                backupDestination = PromptForDirectory("Enter the Backup Destination", mustExist: true);
                if (string.IsNullOrEmpty(backupDestination)) return;
                _pipelineLogger.Log($"User provided Query parameter: BackupDest='{backupDestination}'");
            }

            // Validate directory
            if (!Directory.Exists(backupDestination))
            {
                _pipelineLogger.Log($"[ERROR] Backup destination directory does not exist: '{backupDestination}'");
                Console.WriteLine($"Error: Backup destination not found: {backupDestination}");
                return;
            }

            _pipelineLogger.Log("Initializing storage and loading persisted data for Query.");
            var storage = new ComponentStorage();
            var backupDatesFilePath = Path.Combine(AppContext.BaseDirectory, "backupDates.json");
            _pipelineLogger.Log($"Loading backup dates from '{backupDatesFilePath}'.");
            var backupDates = DataPersistence.LoadBackupDates(backupDatesFilePath);
            _pipelineLogger.Log($"Loaded {backupDates.Count} backup dates.");

            var initialEntity = new Entity();
            // Pass info needed by potential future query systems, although current one doesn't use it.
            storage.SetComponent(initialEntity, new FilePathComponent { FilePath = backupDestination });
            var entities = new List<Entity> { initialEntity };

            _pipelineLogger.Log("Building QueryBackupDates pipeline...");
            var queryPipeline = QueryBackupDatesPipelineBuilder.BuildQueryBackupDatesPipeline(
                backupDates,
                _pipelineLogger);

            _pipelineLogger.Log("Executing QueryBackupDates pipeline...");
            var queryResult = queryPipeline.Execute(entities, storage);

            if (queryResult.IsSuccess)
            {
                var backupDatesComponent = storage.GetComponent<BackupDatesComponent>(initialEntity);
                if (backupDatesComponent != null && backupDatesComponent.Dates.Any())
                {
                    _pipelineLogger.Log($"Query successful. Found {backupDatesComponent.Dates.Count} backup dates.");
                    Console.WriteLine("\nAvailable Backup Dates (Most Recent First):");
                    foreach (var date in backupDatesComponent.Dates) // Dates are sorted descending
                    {
                        Console.WriteLine($"  {date:yyyy-MM-dd HH:mm:ss}");
                        _pipelineLogger.Log($" - Found Date: {date:yyyy-MM-dd HH:mm:ss}");
                    }
                }
                else
                {
                    _pipelineLogger.Log("Query successful, but no backup dates found in persisted data.");
                    Console.WriteLine("\nNo backup dates found.");
                }
            }
            else
            {
                _pipelineLogger.Log($"[ERROR] Failed to query backup dates during pipeline execution: {queryResult.ErrorMessage}");
                Console.WriteLine($"Failed to query backup dates: {queryResult.ErrorMessage}");
            }
            _pipelineLogger.Log("Query operation complete.");
        }

        // --- Duplicate Detection Operation ---
        static void PerformDuplicateDetection(string[] args)
        {
            if (_pipelineLogger == null) { Console.WriteLine("[FATAL ERROR] Logger not initialized. Cannot proceed."); return; }
            _pipelineLogger.Log("Starting Duplicate Detection operation setup.");

            string sourceDirectory;

            if (args.Length >= 1)
            {
                sourceDirectory = args[0];
                _pipelineLogger.Log($"Using command-line argument for Duplicate Detection: Source='{sourceDirectory}'");
                Console.WriteLine("Using command-line argument for Duplicate Detection:");
                Console.WriteLine($"Source Directory: {sourceDirectory}");
            }
            else
            {
                _pipelineLogger.Log("Insufficient command-line arguments. Prompting user for Duplicate Detection parameters.");
                Console.WriteLine("Duplicate Detection requires a source directory.");
                sourceDirectory = PromptForDirectory("Enter the Source Directory", mustExist: true);
                if (string.IsNullOrEmpty(sourceDirectory)) return;
                _pipelineLogger.Log($"User provided Duplicate Detection parameter: Source='{sourceDirectory}'");
            }

            if (!Directory.Exists(sourceDirectory))
            {
                _pipelineLogger.Log($"[ERROR] Source directory does not exist: '{sourceDirectory}'");
                Console.WriteLine($"Error: Source directory not found: {sourceDirectory}");
                return;
            }

            _pipelineLogger.Log("Initializing storage for Duplicate Detection.");
            var storage = new ComponentStorage();
            var root = new Entity();
            storage.SetComponent(root, new FilePathComponent { FilePath = sourceDirectory });
            var entities = new List<Entity> { root };

            _pipelineLogger.Log("Building DuplicateDetection pipeline...");
            var pipeline = DuplicateDetectionPipelineBuilder.BuildDuplicateDetectionPipeline(
                sourceDirectory,
                _pipelineLogger);

            _pipelineLogger.Log("Executing DuplicateDetection pipeline...");
            var result = pipeline.Execute(entities, storage);

            if (result.IsSuccess)
            {
                var dupComponent = storage.GetComponent<DuplicateFilesComponent>(root);
                if (dupComponent != null && dupComponent.Groups.Any())
                {
                    _pipelineLogger.Log($"Duplicate Detection finished. Found {dupComponent.Groups.Count} duplicate groups.");
                    Console.WriteLine($"Found {dupComponent.Groups.Count} groups of duplicate files. See log for details.");
                }
                else
                {
                    _pipelineLogger.Log("Duplicate Detection finished. No duplicates found.");
                    Console.WriteLine("No duplicate files found.");
                }
            }
            else
            {
                _pipelineLogger.Log($"[ERROR] Duplicate Detection pipeline failed: {result.ErrorMessage}");
                Console.WriteLine($"Duplicate detection failed: {result.ErrorMessage}");
            }

            _pipelineLogger.Log("Duplicate Detection operation complete.");
        }

        // --- Helper Methods ---

        /// <summary>
        /// Prompts the user for a directory path with basic validation.
        /// </summary>
        static string PromptForDirectory(string promptMessage, bool mustExist)
        {
            string? inputPath = null;
            bool isValid = false;

            while (!isValid)
            {
                Console.Write($"{promptMessage}: ");
                inputPath = Console.ReadLine()?.Trim();

                if (string.IsNullOrWhiteSpace(inputPath))
                {
                    Console.WriteLine("Input cannot be empty. Please try again or press Enter to cancel.");
                    inputPath = Console.ReadLine()?.Trim(); // Give one more chance or let them cancel
                    if (string.IsNullOrWhiteSpace(inputPath))
                    {
                        _pipelineLogger?.Log($"User cancelled directory input for prompt: '{promptMessage}'");
                        return string.Empty; // Indicate cancellation / invalid input
                    }
                }

                // Check if directory must exist
                if (mustExist && !Directory.Exists(inputPath))
                {
                    Console.WriteLine($"Directory not found: '{inputPath}'. Please enter a valid existing directory.");
                    _pipelineLogger?.Log($"Invalid input (must exist): Directory '{inputPath}' not found.");
                    isValid = false; // Continue loop
                }
                // Check if directory path format is valid (basic check)
                else
                {
                    try
                    {
                        // Attempt to get full path to validate format - this throws exceptions for invalid chars etc.
                        string fullPath = Path.GetFullPath(inputPath);

                        // If it doesn't need to exist, but we need to create it later, try creating it now
                        // to catch permissions issues early (relevant for destination dirs).
                        if (!mustExist)
                        {
                            Directory.CreateDirectory(fullPath); // Try creating it
                        }
                        isValid = true; // Path is valid (exists or creatable)
                        inputPath = fullPath; // Use the cleaned-up full path
                    }
                    catch (ArgumentException) // Handles empty/whitespace paths already caught, but also invalid chars
                    {
                        Console.WriteLine("Invalid path format (e.g., contains illegal characters). Please try again.");
                        _pipelineLogger?.Log($"Invalid input (format): Path '{inputPath}' contains invalid characters.");
                        isValid = false;
                    }
                    catch (NotSupportedException) // e.g., contains colon in the middle
                    {
                        Console.WriteLine("Invalid path format (e.g., colon in the middle). Please try again.");
                        _pipelineLogger?.Log($"Invalid input (format/support): Path '{inputPath}' not supported.");
                        isValid = false;
                    }
                    catch (PathTooLongException)
                    {
                        Console.WriteLine("The specified path is too long. Please try again.");
                        _pipelineLogger?.Log($"Invalid input (length): Path '{inputPath}' is too long.");
                        isValid = false;
                    }
                    catch (IOException ex) // Permissions, device not ready etc. when creating
                    {
                        Console.WriteLine($"An I/O error occurred: {ex.Message}. Check permissions or drive status.");
                        _pipelineLogger?.Log($"[ERROR] IO error accessing/creating path '{inputPath}': {ex.Message}");
                        isValid = false;
                    }
                    catch (UnauthorizedAccessException) // Explicit permission issue
                    {
                        Console.WriteLine($"Permission denied accessing or creating path: '{inputPath}'. Please check permissions.");
                        _pipelineLogger?.Log($"[ERROR] Permission denied for path '{inputPath}'.");
                        isValid = false;
                    }
                    catch (Exception ex) // Catch-all for other unexpected issues during validation/creation
                    {
                        Console.WriteLine($"An unexpected error occurred validating path: {ex.Message}");
                        _pipelineLogger?.Log($"[ERROR] Unexpected error validating path '{inputPath}': {ex.GetType().Name} - {ex.Message}");
                        isValid = false;
                    }
                }
            }

            return inputPath ?? string.Empty; // Should not be null if isValid is true, but satisfy compiler
        }

        /// <summary>
        /// Displays the help information for the command-line tool.
        /// </summary>
        static void DisplayHelp()
        {
            Console.WriteLine("\nDifferentialBackup Utility");
            Console.WriteLine("--------------------------");
            Console.WriteLine("Manages differential backups of a source directory.");
            Console.WriteLine();
            Console.WriteLine("Usage:");
            Console.WriteLine("  DifferentialBackup.exe <operation> [arguments]");
            Console.WriteLine();
            Console.WriteLine("Operations:");
            Console.WriteLine("  backup   Performs a backup. Only files changed since the last backup");
            Console.WriteLine("           are stored as numbered ZIP parts in a timestamped backup folder.");
            Console.WriteLine("           Arguments (Optional - will prompt if missing):");
            Console.WriteLine("             <SourceDirectory>    Path to the directory to back up.");
            Console.WriteLine("             <BackupDestination>  Path to the directory where backup folders will be stored.");
            Console.WriteLine();
            Console.WriteLine("  restore  Restores files from the most recent backup (or --backup-date yyyyMMddHHmmss).");
            Console.WriteLine("           Arguments (Optional - will prompt if missing):");
            Console.WriteLine("             <BackupDestination>  Path where backup folders are stored.");
            Console.WriteLine("             <RestoreDestination> Path to the directory where files will be restored.");
            Console.WriteLine();
            Console.WriteLine("  query    Lists the available backup dates (timestamps) found in the destination.");
            Console.WriteLine("           Arguments (Optional - will prompt if missing):");
            Console.WriteLine("             <BackupDestination>  Path where backup folders are stored.");
            Console.WriteLine();
            Console.WriteLine("  duplicate Detects duplicate files in a directory and logs results.");
            Console.WriteLine("           Arguments (Optional - will prompt if missing):");
            Console.WriteLine("             <SourceDirectory>  Path to the directory to scan for duplicates.");
            Console.WriteLine();
            Console.WriteLine("  help     Displays this help message.");
            Console.WriteLine();
            Console.WriteLine("Examples:");
            Console.WriteLine(@"  DifferentialBackup.exe backup ""C:\My Documents"" ""D:\Backups\MyDocs""");
            Console.WriteLine(@"  DifferentialBackup.exe restore ""D:\Backups\MyDocs"" ""C:\Restored Documents""");
            Console.WriteLine(@"  DifferentialBackup.exe restore ""D:\Backups\MyDocs"" ""C:\Restored Documents"" --backup-date 20260919165347");
            Console.WriteLine(@"  DifferentialBackup.exe query ""D:\Backups\MyDocs""");
            Console.WriteLine(@"  DifferentialBackup.exe duplicate ""C:\My Documents""");
            Console.WriteLine(@"  DifferentialBackup.exe help");
            Console.WriteLine();
            Console.WriteLine("Data Files:");
            Console.WriteLine($"  State information (file hashes, backup dates) is stored in JSON files");
            Console.WriteLine($"  (fileHashes.json, backupDates.json) in the application directory:");
            Console.WriteLine($"  {AppContext.BaseDirectory}");
            Console.WriteLine($"Log File:");
            Console.WriteLine($"  Detailed logs are written to:");
            Console.WriteLine($"  {Path.Combine(AppContext.BaseDirectory, "differential_backup_pipeline.log")}");
            Console.WriteLine();
            Console.WriteLine("Backup retries source paths after 1, 1, 2, 3, 5, 8, and 13 minutes.");
            Console.WriteLine("A backup with captured content and unresolved paths is published with warnings (exit 1).");
            Console.WriteLine("Exit codes: 0 success, 1 warnings/incomplete, 2 failed request or operation, 130 cancelled.");
        }

        private static void SafeLog(string message)
        {
            try
            {
                _pipelineLogger?.Log(message);
            }
            catch
            {
                // Diagnostics cannot replace an operation result.
            }
        }

        private sealed class SafePipelineLogger : IPipelineLogger, IProgressLogger
        {
            private readonly IPipelineLogger _inner;

            public SafePipelineLogger(IPipelineLogger inner)
            {
                _inner = inner;
            }

            public void Log(string message)
            {
                try
                {
                    _inner.Log(message);
                }
                catch
                {
                }
            }

            public void ReportProgress(string message)
            {
                try
                {
                    if (_inner is IProgressLogger progress)
                    {
                        progress.ReportProgress(message);
                    }
                }
                catch
                {
                }
            }

            public void Dispose()
            {
                try
                {
                    _inner.Dispose();
                }
                catch
                {
                }
            }
        }
    }
}
