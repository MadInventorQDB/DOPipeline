using Xunit;
using DifferentialBackup.Pipeline;
using DOPipeline.Storage;
using DOPipeline.Entities;
using System.IO;
using System;
using System.Reflection;
using DifferentialBackup.Test.Helpers;
using DOPipeline.Logging;
using System.Collections.Generic;
using System.Linq;
using DOPipeline.Components;
using System.Globalization;

namespace DifferentialBackup.Test.Pipeline
{
    public class RestorePipelineBuilderTests : IDisposable
    {
        private readonly string _testBasePath;
        private readonly string _backupDestination;
        private readonly string _restoreDestination;
        private readonly IPipelineLogger _logger;
        private const string BackupFolderFormat = "yyyyMMddHHmmss";

        public RestorePipelineBuilderTests()
        {
            _logger = NullLogger.Instance;
            _testBasePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            _backupDestination = Path.Combine(_testBasePath, "Backup");
            _restoreDestination = Path.Combine(_testBasePath, "Restore");
            Directory.CreateDirectory(_backupDestination);
            Directory.CreateDirectory(_restoreDestination);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_testBasePath)) { Directory.Delete(_testBasePath, true); } } catch (Exception ex) { Console.WriteLine($"WARN: Cleanup failed: {ex.Message}"); }
            GC.SuppressFinalize(this);
        }


        [Fact]
        public void BuildRestorePipeline_CreatesPipelineWithCorrectNumberOfPipes()
        {
            // Arrange
            var backupDate = DateTime.UtcNow;

            // Act
            var pipeline = RestorePipelineBuilder.BuildRestorePipeline(
                _backupDestination,
                backupDate,
                _restoreDestination,
                _logger);

            // Assert
            Assert.NotNull(pipeline);
            var pipesField = typeof(DOPipeline.Pipeline.Pipeline).GetField("_pipes", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(pipesField);
            var pipes = pipesField.GetValue(pipeline) as List<DOPipeline.Pipeline.Pipe>;
            Assert.NotNull(pipes);
            Assert.Single(pipes);
        }

        [Fact]
        public void RestorePipeline_Execute_Success_WhenBackupExists()
        {
            // Arrange
            var backupDate = DateTime.UtcNow;
            var backupFolderName = backupDate.ToString(BackupFolderFormat);
            var backupFolderPath = Path.Combine(_backupDestination, backupFolderName);
            Directory.CreateDirectory(backupFolderPath);

            var backupFile1 = Path.Combine(backupFolderPath, "restore_test1.txt");
            var subDir = Path.Combine(backupFolderPath, "SubFolder");
            Directory.CreateDirectory(subDir);
            var backupFile2 = Path.Combine(subDir, "restore_test2.log");
            File.WriteAllText(backupFile1, "Content for File 1");
            File.WriteAllText(backupFile2, "Logging data");

            var pipeline = RestorePipelineBuilder.BuildRestorePipeline(_backupDestination, backupDate, _restoreDestination, _logger);
            var storage = new ComponentStorage();
            var initialEntity = new Entity();
            var entities = new List<Entity> { initialEntity };

            // Act
            var result = pipeline.Execute(entities, storage);

            // Assert
            Assert.True(result.IsSuccess, $"Restore pipeline execution should succeed.");
            var errorComponent = storage.GetComponent<ErrorComponent>(initialEntity);
            Assert.Null(errorComponent);

            var restoredFile1 = Path.Combine(_restoreDestination, "restore_test1.txt");
            var restoredSubDir = Path.Combine(_restoreDestination, "SubFolder");
            var restoredFile2 = Path.Combine(restoredSubDir, "restore_test2.log");
            Assert.True(File.Exists(restoredFile1), $"Restored file '{restoredFile1}' should exist.");
            Assert.Equal("Content for File 1", File.ReadAllText(restoredFile1));
            Assert.True(Directory.Exists(restoredSubDir), $"Restored subdirectory '{restoredSubDir}' should exist.");
            Assert.True(File.Exists(restoredFile2), $"Restored file '{restoredFile2}' should exist.");
            Assert.Equal("Logging data", File.ReadAllText(restoredFile2));
        }

        [Fact]
        public void RestorePipeline_Execute_AddsErrorComponent_WhenBackupDateFolderMissing()
        {
            // Arrange
            var backupDate = DateTime.UtcNow.AddDays(-1);
            var nonExistentFolderPath = Path.Combine(_backupDestination, backupDate.ToString(BackupFolderFormat));
            if (Directory.Exists(nonExistentFolderPath)) { Directory.Delete(nonExistentFolderPath, true); }

            var pipeline = RestorePipelineBuilder.BuildRestorePipeline(
                _backupDestination,
                backupDate,
                _restoreDestination,
                _logger);

            var storage = new ComponentStorage();
            var initialEntity = new Entity();
            var entities = new List<Entity> { initialEntity };

            // Act
            var result = pipeline.Execute(entities, storage);

            // Assert
            Assert.True(result.IsSuccess);
            var errorComponent = storage.GetComponent<ErrorComponent>(initialEntity);
            Assert.NotNull(errorComponent);
            Assert.Equal("Backup date not found.", errorComponent.ErrorMessage);
            Assert.Empty(Directory.GetFiles(_restoreDestination, "*", SearchOption.AllDirectories));
            Assert.Empty(Directory.GetDirectories(_restoreDestination));
        }
    }
}