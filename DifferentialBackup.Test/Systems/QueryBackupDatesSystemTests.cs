using Xunit;
using DifferentialBackup.Systems;
using DOPipeline.Entities;
using DOPipeline.Storage;
using DifferentialBackup.Components;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DifferentialBackup.Test.Systems
{
    public class QueryBackupDatesSystemTests
    {
        [Fact]
        public void Execute_SetsBackupDatesComponent_SystemsTest()
        {
            // Arrange
            var backupDatesSet = new HashSet<DateTime>
            {
                new DateTime(2022, 1, 2),
                new DateTime(2022, 1, 1),
                new DateTime(2022, 1, 3)
            };

            var system = new QueryBackupDatesSystem(backupDatesSet);

            var entity = new Entity();
            var storage = new ComponentStorage();

            // Act
            var result = system.Execute(entity, storage);

            // Assert
            Assert.True(result.IsSuccess);
            var backupDatesComponent = storage.GetComponent<BackupDatesComponent>(entity);
            Assert.NotNull(backupDatesComponent);
            var expectedDates = backupDatesSet.OrderByDescending(d => d).ToList();
            Assert.Equal(expectedDates, backupDatesComponent.Dates);
        }

        [Fact]
        public void Execute_EmptyBackupDates_SetsEmptyComponent_SystemsTest()
        {
            // Arrange
            var backupDatesSet = new HashSet<DateTime>();

            var system = new QueryBackupDatesSystem(backupDatesSet);

            var entity = new Entity();
            var storage = new ComponentStorage();

            // Act
            var result = system.Execute(entity, storage);

            // Assert
            Assert.True(result.IsSuccess);
            var backupDatesComponent = storage.GetComponent<BackupDatesComponent>(entity);
            Assert.NotNull(backupDatesComponent);
            Assert.Empty(backupDatesComponent.Dates);
        }
    }
}
