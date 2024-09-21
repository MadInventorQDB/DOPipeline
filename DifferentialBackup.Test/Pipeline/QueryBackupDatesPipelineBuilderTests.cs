using Xunit;
using DifferentialBackup.Pipeline;
using DOPipeline.Storage;
using DOPipeline.Entities;
using System;
using System.Collections.Generic;
using DifferentialBackup.Components;
using System.Linq;
using System.Reflection;

namespace DifferentialBackup.Test.Pipeline
{
    public class QueryBackupDatesPipelineBuilderTests
    {
        [Fact]
        public void BuildQueryBackupDatesPipeline_CreatesPipeline()
        {
            // Arrange
            var backupDates = new HashSet<DateTime>();

            // Act
            var pipeline = QueryBackupDatesPipelineBuilder.BuildQueryBackupDatesPipeline(backupDates);

            // Assert
            Assert.NotNull(pipeline);

            var pipesField = typeof(DOPipeline.Pipeline.Pipeline).GetField("_pipes", BindingFlags.NonPublic | BindingFlags.Instance);
            var pipes = (List<DOPipeline.Pipeline.Pipe>)pipesField.GetValue(pipeline);
            Assert.Single(pipes);
        }

        [Fact]
        public void QueryBackupDatesPipeline_Execute_SetsBackupDatesComponent_PipelineTest()
        {
            // Arrange
            var backupDatesSet = new HashSet<DateTime>
            {
                new DateTime(2022, 1, 2),
                new DateTime(2022, 1, 1),
                new DateTime(2022, 1, 3)
            };

            var pipeline = QueryBackupDatesPipelineBuilder.BuildQueryBackupDatesPipeline(backupDatesSet);

            var storage = new ComponentStorage();
            var initialEntity = new Entity();
            var entities = new List<Entity> { initialEntity };

            // Act
            var result = pipeline.Execute(entities, storage);

            // Assert
            Assert.True(result.IsSuccess);
            var backupDatesComponent = storage.GetComponent<BackupDatesComponent>(initialEntity);
            Assert.NotNull(backupDatesComponent);
            var expectedDates = backupDatesSet.OrderByDescending(d => d).ToList();
            Assert.Equal(expectedDates, backupDatesComponent.Dates);
        }
    }
}
