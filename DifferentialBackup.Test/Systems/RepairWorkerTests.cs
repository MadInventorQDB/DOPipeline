using DifferentialBackup.Components;
using DifferentialBackup.Systems;
using DifferentialBackup.Utilities;
using DOPipeline.Logging;
using Xunit;

namespace DifferentialBackup.Test.Systems;

public sealed partial class BackupRecoveryRegressionTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BackupWorkersRetainFirstFailureAndJoinBothWorkers(bool producerFirst)
    {
        var p = RepairPaths();
        var a = Path.Combine(p.Source, "a.txt");
        var b = Path.Combine(p.Source, "b.txt");
        File.WriteAllText(a, "AAAA"); File.WriteAllText(b, "BBBB");
        var date = DateTime.UtcNow;
        var world = CreateWorld(p.Source, p.Destination, a, Hash(a), date);
        AddFile(world, p.Source, b, Hash(b), date);
        using var observer = new OrderedWorkerFaults(producerFirst);
        var state = new BackupRunState(p.Source, p.Destination, p.Staging, observer);
        state.BeginRun();
        var options = new BackupBatchOptions { MaxFilesPerPart = 1, TransferQueueCapacity = 1 };
        Assert.True(new BackupPartPlanningSystem(p.Source, p.Destination, state, options).Execute(world.GetAllEntities(), world).IsSuccess);
        var result = await Task.Run(() => new BackupExecutionSystem(state, options, new AlwaysThrowingLogger())
            .Execute(world.GetAllEntities(), world)).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.False(result.IsSuccess);
        Assert.False(result.IsCancellation);
        Assert.Same(observer.First, result.Exception);
        Assert.Equal(observer.First.Message, result.ErrorMessage);
        Assert.True(observer.ProducerFailed);
        Assert.True(observer.ConsumerFailed);
        var op = world.Query<OperationComponent>().Single();
        world.SetComponent(op, new FatalFailureComponent
        {
            RunId = world.GetComponent<OperationComponent>(op)!.RunId, Message = result.ErrorMessage!
        });
        world.SetComponent(op, new CancellationSignalComponent { RunId = world.GetComponent<OperationComponent>(op)!.RunId });
        Assert.True(new OperationSummarySystem().Execute(new[] { op }, world).IsSuccess);
        Assert.Equal(2, world.GetComponent<OperationOutcomeComponent>(op)!.ExitCode);
    }

    private sealed class OrderedWorkerFaults(bool producerFirst) : ICheckpointObserver, IDisposable
    {
        public readonly Exception First = new IOException("first worker failure");
        private readonly Exception _second = new IOException("second worker failure");
        private readonly ManualResetEventSlim _recorded = new();
        private int _parts;
        public bool ProducerFailed, ConsumerFailed;
        public void Reached(string checkpoint)
        {
            if (checkpoint == "producer-failure-recorded") { ProducerFailed = true; if (producerFirst) _recorded.Set(); }
            if (checkpoint == "consumer-failure-recorded") { ConsumerFailed = true; if (!producerFirst) _recorded.Set(); }
            if (checkpoint == "producer-part-starting" && Interlocked.Increment(ref _parts) == 2)
            {
                if (!producerFirst && !_recorded.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("consumer failure missing");
                throw producerFirst ? First : _second;
            }
            if (checkpoint == "transfer-part-starting")
            {
                if (producerFirst && !_recorded.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("producer failure missing");
                throw producerFirst ? _second : First;
            }
        }
        public void Dispose() => _recorded.Dispose();
    }

    private sealed class AlwaysThrowingLogger : IPipelineLogger, IProgressLogger
    {
        public void Log(string text) => throw new IOException("logger failure");
        public void ReportProgress(string text) => Log(text);
        public void Dispose() => throw new IOException("logger disposal failure");
    }
}
