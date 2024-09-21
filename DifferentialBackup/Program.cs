using DifferentialBackup.Components;
using DifferentialBackup.Pipeline;
using DifferentialBackup.Utilities;
using DOPipeline.Entities;
using DOPipeline.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DifferentialBackup
{
    internal class Program
    {
        enum Operation
        {
            Backup,
            Restore,
            Query,
            Help
        }

        static void Main(string[] args)
        {
            if (args.Length == 0)
            {
                DisplayHelp();
                return;
            }

            // Parse the operation
            if (!Enum.TryParse(args[0], true, out Operation operation))
            {
                Console.WriteLine($"Invalid operation: {args[0]}");
                DisplayHelp();
                return;
            }

            try
            {
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
                    case Operation.Help:
                    default:
                        DisplayHelp();
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"An unexpected error occurred: {ex.Message}");
            }
        }

        static void PerformBackup(string[] args)
        {
            string sourceDirectory;
            string backupDestination;

            if (args.Length >= 2)
            {
                sourceDirectory = args[0];
                backupDestination = args[1];
                Console.WriteLine("Using command-line arguments for Backup:");
                Console.WriteLine($"Source Directory: {sourceDirectory}");
                Console.WriteLine($"Backup Destination: {backupDestination}");
            }
            else
            {
                Console.WriteLine("Backup operation requires Source Directory and Backup Destination.");

                // Prompt for Source Directory
                while (true)
                {
                    Console.Write("Enter the Source Directory: ");
                    string? input = Console.ReadLine()?.Trim();
                    if (!string.IsNullOrEmpty(input) && Directory.Exists(input))
                    {
                        sourceDirectory = input;
                        break;
                    }
                    Console.WriteLine("Invalid directory. Please try again.");
                }

                // Prompt for Backup Destination
                while (true)
                {
                    Console.Write("Enter the Backup Destination: ");
                    string? input = Console.ReadLine()?.Trim();
                    if (!string.IsNullOrEmpty(input))
                    {
                        try
                        {
                            // Attempt to create the directory if it doesn't exist
                            Directory.CreateDirectory(input);
                            backupDestination = input;
                            break;
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Error creating directory: {ex.Message}");
                        }
                    }
                    else
                    {
                        Console.WriteLine("Backup destination cannot be empty. Please try again.");
                    }
                }
            }

            // Initialize storage and load persisted data
            var storage = new ComponentStorage();

            // File paths for persisted data
            var hashesFilePath = Path.Combine(AppContext.BaseDirectory, "fileHashes.json");
            var backupDatesFilePath = Path.Combine(AppContext.BaseDirectory, "backupDates.json");

            // Load persisted data
            var fileHashes = DataPersistence.LoadFileHashes(hashesFilePath);
            var backupDates = DataPersistence.LoadBackupDates(backupDatesFilePath);

            var initialEntity = new Entity();
            storage.SetComponent(initialEntity, new FilePathComponent { FilePath = sourceDirectory });
            var entities = new List<Entity> { initialEntity };

            // Build and execute the backup pipeline
            var backupPipeline = BackupPipelineBuilder.BuildBackupPipeline(
                sourceDirectory,
                backupDestination,
                fileHashes,
                backupDates);
            var backupResult = backupPipeline.Execute(entities, storage);

            if (backupResult.IsSuccess)
                Console.WriteLine("Backup completed successfully.");
            else
                Console.WriteLine($"Backup failed: {backupResult.ErrorMessage}");

            // Save persisted data
            DataPersistence.SaveFileHashes(fileHashes, hashesFilePath);
            DataPersistence.SaveBackupDates(backupDates, backupDatesFilePath);
        }

        static void PerformRestore(string[] args)
        {
            string backupDestination;
            string restoreDestination;

            if (args.Length >= 2)
            {
                backupDestination = args[0];
                restoreDestination = args[1];
                Console.WriteLine("Using command-line arguments for Restore:");
                Console.WriteLine($"Backup Destination: {backupDestination}");
                Console.WriteLine($"Restore Destination: {restoreDestination}");
            }
            else
            {
                Console.WriteLine("Restore operation requires Backup Destination and Restore Destination.");

                // Prompt for Backup Destination
                while (true)
                {
                    Console.Write("Enter the Backup Destination: ");
                    string? input = Console.ReadLine()?.Trim();
                    if (!string.IsNullOrEmpty(input) && Directory.Exists(input))
                    {
                        backupDestination = input;
                        break;
                    }
                    Console.WriteLine("Invalid backup destination directory. Please try again.");
                }

                // Prompt for Restore Destination
                while (true)
                {
                    Console.Write("Enter the Restore Destination: ");
                    string? input = Console.ReadLine()?.Trim();
                    if (!string.IsNullOrEmpty(input))
                    {
                        try
                        {
                            // Attempt to create the directory if it doesn't exist
                            Directory.CreateDirectory(input);
                            restoreDestination = input;
                            break;
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Error creating directory: {ex.Message}");
                        }
                    }
                    else
                    {
                        Console.WriteLine("Restore destination cannot be empty. Please try again.");
                    }
                }
            }

            // Initialize storage and load persisted data
            var storage = new ComponentStorage();

            // File paths for persisted data
            var backupDatesFilePath = Path.Combine(AppContext.BaseDirectory, "backupDates.json");

            // Load persisted data
            var backupDates = DataPersistence.LoadBackupDates(backupDatesFilePath);

            var initialEntity = new Entity();
            storage.SetComponent(initialEntity, new FilePathComponent { FilePath = backupDestination });
            var entities = new List<Entity> { initialEntity };

            // Build and execute the query backup dates pipeline to find the latest backup date
            var queryPipeline = QueryBackupDatesPipelineBuilder.BuildQueryBackupDatesPipeline(backupDates);
            var queryResult = queryPipeline.Execute(entities, storage);

            if (queryResult.IsSuccess)
            {
                var backupDatesComponent = storage.GetComponent<BackupDatesComponent>(initialEntity);
                if (backupDatesComponent != null && backupDatesComponent.Dates.Any())
                {
                    var latestBackupDate = backupDatesComponent.Dates.First();

                    // Build and execute the restore pipeline
                    var restorePipeline = RestorePipelineBuilder.BuildRestorePipeline(
                        backupDestination,
                        latestBackupDate,
                        restoreDestination);
                    var restoreResult = restorePipeline.Execute(entities, storage);

                    if (restoreResult.IsSuccess)
                        Console.WriteLine("Restore completed successfully.");
                    else
                        Console.WriteLine($"Restore failed: {restoreResult.ErrorMessage}");
                }
                else
                {
                    Console.WriteLine("No backup dates found.");
                }
            }
            else
            {
                Console.WriteLine($"Failed to query backup dates: {queryResult.ErrorMessage}");
            }
        }

        static void PerformQuery(string[] args)
        {
            string backupDestination;

            if (args.Length >= 1)
            {
                backupDestination = args[0];
                Console.WriteLine("Using command-line argument for Query:");
                Console.WriteLine($"Backup Destination: {backupDestination}");
            }
            else
            {
                Console.WriteLine("Query operation requires Backup Destination.");

                // Prompt for Backup Destination
                while (true)
                {
                    Console.Write("Enter the Backup Destination: ");
                    string? input = Console.ReadLine()?.Trim();
                    if (!string.IsNullOrEmpty(input) && Directory.Exists(input))
                    {
                        backupDestination = input;
                        break;
                    }
                    Console.WriteLine("Invalid backup destination directory. Please try again.");
                }
            }

            // Initialize storage and load persisted data
            var storage = new ComponentStorage();

            // File paths for persisted data
            var backupDatesFilePath = Path.Combine(AppContext.BaseDirectory, "backupDates.json");

            // Load persisted data
            var backupDates = DataPersistence.LoadBackupDates(backupDatesFilePath);

            var initialEntity = new Entity();
            storage.SetComponent(initialEntity, new FilePathComponent { FilePath = backupDestination });
            var entities = new List<Entity> { initialEntity };

            // Build and execute the query backup dates pipeline
            var queryPipeline = QueryBackupDatesPipelineBuilder.BuildQueryBackupDatesPipeline(backupDates);
            var queryResult = queryPipeline.Execute(entities, storage);

            if (queryResult.IsSuccess)
            {
                var backupDatesComponent = storage.GetComponent<BackupDatesComponent>(initialEntity);
                if (backupDatesComponent != null && backupDatesComponent.Dates.Any())
                {
                    Console.WriteLine("Available Backup Dates:");
                    foreach (var date in backupDatesComponent.Dates)
                    {
                        Console.WriteLine(date.ToString("yyyy-MM-dd HH:mm:ss"));
                    }
                }
                else
                {
                    Console.WriteLine("No backup dates found.");
                }
            }
            else
            {
                Console.WriteLine($"Failed to query backup dates: {queryResult.ErrorMessage}");
            }
        }

        static void DisplayHelp()
        {
            Console.WriteLine("DifferentialBackup Utility");
            Console.WriteLine();
            Console.WriteLine("Usage:");
            Console.WriteLine("  DifferentialBackup.exe <operation> [arguments]");
            Console.WriteLine();
            Console.WriteLine("Operations:");
            Console.WriteLine("  backup   Perform a backup operation.");
            Console.WriteLine("           Arguments:");
            Console.WriteLine("             <SourceDirectory> <BackupDestination>");
            Console.WriteLine("  restore  Perform a restore operation.");
            Console.WriteLine("           Arguments:");
            Console.WriteLine("             <BackupDestination> <RestoreDestination>");
            Console.WriteLine("  query    Query available backup dates.");
            Console.WriteLine("           Arguments:");
            Console.WriteLine("             <BackupDestination>");
            Console.WriteLine("  help     Display this help message.");
            Console.WriteLine();
            Console.WriteLine("Examples:");
            Console.WriteLine(@"  DifferentialBackup.exe backup ""C:\SourceDirectory"" ""F:\BackupDestination""");
            Console.WriteLine(@"  DifferentialBackup.exe restore ""F:\BackupDestination"" ""C:\RestoreDestination""");
            Console.WriteLine(@"  DifferentialBackup.exe query ""F:\BackupDestination""");
        }
    }
}
