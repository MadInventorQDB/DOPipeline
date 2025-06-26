using DifferentialBackup.Components;
using DifferentialBackup.Pipeline;
using DifferentialBackup.Utilities;
using DOPipeline.Entities;
using DOPipeline.Logging; // <-- Added using
using DOPipeline.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

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
            _pipelineLogger = new FileConsoleLogger(logFilePath);
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
                _pipelineLogger?.Log(errorMessage);
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("\nAn unexpected error occurred. Check the log file for details.");
                Console.WriteLine($"Error: {ex.Message}");
                Console.ResetColor();
                // Consider setting an error exit code: Environment.ExitCode = 1;
            }
            finally
            {
                // --- Dispose Logger ---
                // Ensure disposal happens even if errors occurred
                _pipelineLogger?.Dispose();
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
                if (string.IsNullOrEmpty(sourceDirectory)) return; // Exit if user cancelled or input invalid

                // Prompt for Backup Destination
                backupDestination = PromptForDirectory("Enter the Backup Destination", mustExist: false);
                if (string.IsNullOrEmpty(backupDestination)) return; // Exit if user cancelled or input invalid
                _pipelineLogger.Log($"User provided Backup parameters: Source='{sourceDirectory}', Destination='{backupDestination}'");
            }

            // Validate directories after getting them
            if (!Directory.Exists(sourceDirectory))
            {
                _pipelineLogger.Log($"[ERROR] Source directory does not exist: '{sourceDirectory}'");
                Console.WriteLine($"Error: Source directory not found: {sourceDirectory}");
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
                return;
            }


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

            // The initial entity acts as a starting point for the FileDiscoverySystem
            var initialEntity = new Entity();
            var entities = new List<Entity> { initialEntity };

            _pipelineLogger.Log("Building Backup pipeline...");
            // Build the backup pipeline
            var backupPipeline = BackupPipelineBuilder.BuildBackupPipeline(
                sourceDirectory,
                backupDestination,
                fileHashes,
                backupDates,
                _pipelineLogger); // <-- Pass logger

            _pipelineLogger.Log("Executing Backup pipeline...");
            // Execute the pipeline
            var backupResult = backupPipeline.Execute(entities, storage);

            // Backup pipeline itself usually returns Success unless there's a fundamental flaw.
            // Individual file backup errors are handled within the BackupExecutionSystem and logged by the pipeline runner.
            // We check the backupDates set to see if any new backup actually occurred.
            bool newBackupOccurred = backupDates.Any(d => (DateTime.UtcNow - d).TotalMinutes < 1); // Check if a recent date was added

            if (newBackupOccurred)
            {
                _pipelineLogger.Log("Backup completed. New backup version created.");
                Console.WriteLine("Backup completed successfully.");
            }
            else
            {
                _pipelineLogger.Log("Backup process finished. No changes detected or no files needed backup.");
                Console.WriteLine("Backup finished. No changes detected.");
            }


            _pipelineLogger.Log("Saving potentially updated persisted data...");
            // Save potentially updated persisted data
            DataPersistence.SaveFileHashes(fileHashes, hashesFilePath);
            DataPersistence.SaveBackupDates(backupDates, backupDatesFilePath);
            _pipelineLogger.Log("Backup operation complete.");
        }

        // --- Restore Operation ---
        static void PerformRestore(string[] args)
        {
            if (_pipelineLogger == null) { Console.WriteLine("[FATAL ERROR] Logger not initialized. Cannot proceed."); return; }
            _pipelineLogger.Log("Starting Restore operation setup.");

            string backupDestination;
            string restoreDestination;

            // Get parameters
            if (args.Length >= 2)
            {
                backupDestination = args[0];
                restoreDestination = args[1];
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
                if (string.IsNullOrEmpty(backupDestination)) return;

                restoreDestination = PromptForDirectory("Enter the Restore Destination", mustExist: false);
                if (string.IsNullOrEmpty(restoreDestination)) return;
                _pipelineLogger.Log($"User provided Restore parameters: BackupDest='{backupDestination}', RestoreDest='{restoreDestination}'");
            }

            // Validate directories
            if (!Directory.Exists(backupDestination))
            {
                _pipelineLogger.Log($"[ERROR] Backup destination directory does not exist: '{backupDestination}'");
                Console.WriteLine($"Error: Backup destination not found: {backupDestination}");
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
                return;
            }


            _pipelineLogger.Log("Initializing storage and loading backup dates data for Restore.");
            // Initialize storage and load backup dates
            var storage = new ComponentStorage();
            var backupDatesFilePath = Path.Combine(AppContext.BaseDirectory, "backupDates.json");
            _pipelineLogger.Log($"Loading backup dates from '{backupDatesFilePath}'.");
            var backupDates = DataPersistence.LoadBackupDates(backupDatesFilePath);
            _pipelineLogger.Log($"Loaded {backupDates.Count} backup dates.");

            if (!backupDates.Any())
            {
                _pipelineLogger.Log("No backup dates found in persisted data. Cannot perform restore.");
                Console.WriteLine("No backup history found. Cannot restore.");
                return;
            }

            // We use the Query pipeline first to present available dates or find the latest
            var initialEntity = new Entity();
            // The QueryBackupDatesSystem doesn't strictly need this, but consistency is okay.
            storage.SetComponent(initialEntity, new FilePathComponent { FilePath = backupDestination });
            var entities = new List<Entity> { initialEntity };

            _pipelineLogger.Log("Building QueryBackupDates pipeline to find available dates for Restore...");
            var queryPipeline = QueryBackupDatesPipelineBuilder.BuildQueryBackupDatesPipeline(
                backupDates,
                _pipelineLogger);

            _pipelineLogger.Log("Executing QueryBackupDates pipeline for Restore...");
            var queryResult = queryPipeline.Execute(entities, storage);

            if (queryResult.IsSuccess)
            {
                var backupDatesComponent = storage.GetComponent<BackupDatesComponent>(initialEntity);
                if (backupDatesComponent != null && backupDatesComponent.Dates.Any())
                {
                    // For now, let's assume we always restore the latest.
                    // TODO: Could add logic here to prompt user to select a date from backupDatesComponent.Dates
                    var latestBackupDate = backupDatesComponent.Dates.First(); // Dates are sorted descending by the query system
                    _pipelineLogger.Log($"Identified latest backup date for restore: {latestBackupDate:yyyy-MM-dd HH:mm:ss}");

                    _pipelineLogger.Log("Building Restore pipeline...");
                    var restorePipeline = RestorePipelineBuilder.BuildRestorePipeline(
                        backupDestination,
                        latestBackupDate,
                        restoreDestination,
                        _pipelineLogger);

                    _pipelineLogger.Log("Executing Restore pipeline...");
                    var restoreResult = restorePipeline.Execute(entities, storage); // RestoreSystem operates based on constructor args

                    if (restoreResult.IsSuccess)
                    {
                        _pipelineLogger.Log("Restore pipeline completed successfully.");
                        Console.WriteLine("Restore completed successfully.");
                    }
                    else
                    {
                        // RestoreSystem provides specific error messages on failure
                        _pipelineLogger.Log($"[ERROR] Restore pipeline failed: {restoreResult.ErrorMessage}");
                        Console.WriteLine($"Restore failed: {restoreResult.ErrorMessage}");
                    }
                }
                else
                {
                    _pipelineLogger.Log("Query completed, but no backup dates were found in the component (this shouldn't happen if initial load succeeded).");
                    Console.WriteLine("No backup dates found to restore from.");
                }
            }
            else
            {
                // This indicates a failure within the Query pipeline itself.
                _pipelineLogger.Log($"[ERROR] Failed to query backup dates: {queryResult.ErrorMessage}");
                Console.WriteLine($"Failed to query backup dates: {queryResult.ErrorMessage}");
            }
            _pipelineLogger.Log("Restore operation complete.");
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
            Console.WriteLine("           are copied to a new timestamped folder in the destination.");
            Console.WriteLine("           Arguments (Optional - will prompt if missing):");
            Console.WriteLine("             <SourceDirectory>    Path to the directory to back up.");
            Console.WriteLine("             <BackupDestination>  Path to the directory where backup folders will be stored.");
            Console.WriteLine();
            Console.WriteLine("  restore  Restores files from the most recent backup.");
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
        }
    }
}