using Xunit;
using DifferentialBackup.Pipeline;
using DOPipeline.Storage;
using DOPipeline.Entities;
using System;
using System.Collections.Generic;
using DifferentialBackup.Components;
using System.Linq;
using System.Reflection;
// using DOPipeline.Test.Helpers; // <-- Remove this or comment out
using DifferentialBackup.Test.Helpers; // <-- Add this using
using DOPipeline.Logging;

namespace DifferentialBackup.Test.Pipeline
{
    public class QueryBackupDatesPipelineBuilderTests
    {
        private readonly IPipelineLogger _logger = NullLogger.Instance; // Now refers to DifferentialBackup.Test.Helpers.NullLogger

        // --- Test methods remain the same ---

        [Fact]
        public void BuildQueryBackupDatesPipeline_CreatesPipelineWithCorrectNumberOfPipes()
        {
            // Arrange
            var backupDates = new HashSet<DateTime>();

            // Act
            var pipeline = QueryBackupDatesPipelineBuilder.BuildQueryBackupDatesPipeline(
                backupDates,
                _logger); // <-- Pass logger

            // Assert
            Assert.NotNull(pipeline);
            var pipesField = typeof(DOPipeline.Pipeline.Pipeline).GetField("_pipes", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(pipesField);
            var pipes = pipesField.GetValue(pipeline) as List<DOPipeline.Pipeline.Pipe>;
            Assert.NotNull(pipes);
            Assert.Single(pipes);
        }

        [Fact]
        public void QueryBackupDatesPipeline_Execute_SetsBackupDatesComponentSortedDescending()
        {
            // Arrange
            var date1 = new DateTime(2023, 1, 1, 10, 0, 0);
            var date2 = new DateTime(2023, 1, 3, 12, 0, 0);
            var date3 = new DateTime(2023, 1, 2, 11, 0, 0);
            var backupDatesSet = new HashSet<DateTime> { date1, date2, date3 };
            var pipeline = QueryBackupDatesPipelineBuilder.BuildQueryBackupDatesPipeline(backupDatesSet, _logger);
            var storage = new ComponentStorage();
            var initialEntity = new Entity();
            var entities = new List<Entity> { initialEntity };

            // Act
            var result = pipeline.Execute(entities, storage);

            // Assert
            Assert.True(result.IsSuccess);
            var backupDatesComponent = storage.GetComponent<BackupDatesComponent>(initialEntity);
            Assert.NotNull(backupDatesComponent);
            Assert.NotNull(backupDatesComponent.Dates);
            Assert.Equal(3, backupDatesComponent.Dates.Count);
            Assert.Equal(date2, backupDatesComponent.Dates[0]); // Latest
            Assert.Equal(date3, backupDatesComponent.Dates[1]);
            Assert.Equal(date1, backupDatesComponent.Dates[2]); // Earliest
        }

        [Fact]
        public void QueryBackupDatesPipeline_Execute_HandlesEmptySet()
        {
            // Arrange
            var backupDatesSet = new HashSet<DateTime>();
            var pipeline = QueryBackupDatesPipelineBuilder.BuildQueryBackupDatesPipeline(backupDatesSet, _logger);
            var storage = new ComponentStorage();
            var initialEntity = new Entity();
            var entities = new List<Entity> { initialEntity };

            // Act
            var result = pipeline.Execute(entities, storage);

            // Assert
            Assert.True(result.IsSuccess);
            var backupDatesComponent = storage.GetComponent<BackupDatesComponent>(initialEntity);
            Assert.NotNull(backupDatesComponent);
            Assert.NotNull(backupDatesComponent.Dates);
            Assert.Empty(backupDatesComponent.Dates);
        }
    }
}