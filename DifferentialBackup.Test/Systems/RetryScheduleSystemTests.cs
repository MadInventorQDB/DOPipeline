using DOPipeline.Entities;
using DOPipeline.Storage;
using DifferentialBackup.Components;
using DifferentialBackup.Systems;
using DifferentialBackup.Utilities;
using Xunit;

namespace DifferentialBackup.Test.Systems;

public sealed class RetryScheduleSystemTests
{
    [Fact]
    public void PassCompletion_UsesSharedFibonacciSchedule()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero));
        var storage = new ComponentStorage();
        var operationEntity = new Entity();
        var operation = new OperationComponent
        {
            RunId = Guid.NewGuid(),
            Phase = OperationPhase.InitialPass,
            ActivePass = 0
        };
        storage.SetComponent(operationEntity, operation);
        storage.SetComponent(operationEntity, new RetryScheduleComponent { RunId = operation.RunId });
        var fileEntity = new Entity();
        storage.SetComponent(fileEntity, new FileWorkComponent
        {
            RunId = operation.RunId,
            StableKey = "C:\\source\\locked.bin",
            SourcePath = "C:\\source\\locked.bin",
            State = FileWorkState.Deferred
        });
        storage.SetComponent(fileEntity, new BackupIssueComponent
        {
            RunId = operation.RunId,
            StableKey = "C:\\source\\locked.bin",
            Path = "C:\\source\\locked.bin",
            Retryable = true
        });

        var completion = new PassCompletionSystem(clock);
        var activation = new RetryActivationSystem();
        var delays = new List<int>();

        for (var round = 0; round < 7; round++)
        {
            Assert.True(completion.Execute(storage.GetAllEntities(), storage).IsSuccess);
            var schedule = storage.GetComponent<RetryScheduleComponent>(operationEntity)!;
            delays.Add((int)(schedule.NextAttemptUtc!.Value - clock.UtcNow).TotalMinutes);
            operation.Phase = OperationPhase.RetryPass;
            operation.ActivePass = round + 1;
            storage.SetComponent(operationEntity, operation);
            Assert.True(activation.Execute(storage.GetAllEntities(), storage).IsSuccess);
            var work = storage.GetComponent<FileWorkComponent>(fileEntity)!;
            work.State = FileWorkState.Deferred;
            work.LastAttemptedPass = operation.ActivePass;
            storage.SetComponent(fileEntity, work);
        }

        Assert.True(completion.Execute(storage.GetAllEntities(), storage).IsSuccess);
        Assert.Equal(new[] { 1, 1, 2, 3, 5, 8, 13 }, delays);
        Assert.Equal(7, storage.GetComponent<RetryScheduleComponent>(operationEntity)!.CompletedRound);
    }

    private sealed class ManualClock : IClock
    {
        public ManualClock(DateTimeOffset now) => UtcNow = now;
        public DateTimeOffset UtcNow { get; private set; }
    }
}
