using DOPipeline.Components;
using DOPipeline.Entities;
using DOPipeline.Logging;
using DOPipeline.Pipeline;
using DOPipeline.Storage;
using DOPipeline.Systems;
using DOPipeline.Utilities;
using Xunit;

namespace DOPipeline.Test.Pipeline;

public sealed class FailurePrecedenceTests
{
    [Theory]
    [InlineData("fatal", "cancel", false)]
    [InlineData("fatal", "cancel", true)]
    [InlineData("fatal", "fail", false)]
    [InlineData("fatal", "fail", true)]
    [InlineData("cancel", "fail", false)]
    [InlineData("cancel", "fail", true)]
    [InlineData("cancel", "success", false)]
    [InlineData("cancel", "success", true)]
    [InlineData("success", "fail", false)]
    [InlineData("success", "fail", true)]
    [InlineData("success", "success", false)]
    [InlineData("success", "success", true)]
    public void ExecutionAndCleanupPrecedence(string execution, string cleanup, bool brokenLogger)
    {
        using var cancellation = new CancellationTokenSource();
        var initiating = new IOException("original execution failure");
        var cleanupError = new IOException("cleanup failure");
        var scoped = new DelegateScope(() =>
        {
            if (execution == "fatal") return Result.Fail(initiating.Message, initiating);
            if (execution == "cancel") { cancellation.Cancel(); cancellation.Token.ThrowIfCancellationRequested(); }
            return Result.Success();
        }, () =>
        {
            if (cleanup == "cancel") cancellation.Cancel();
            if (cleanup == "fail") throw cleanupError;
            return Result.Success();
        });
        var later = new DelegateScope(() => Result.Success(), () => Result.Success());
        var logger = new TestLogger(brokenLogger);
        var pipeline = new DOPipeline.Pipeline.Pipeline(logger)
            .AddPipe(new Pipe("execution").AddSystem(scoped))
            .AddPipe(new Pipe("later").AddSystem(later));
        var row = new Entity();
        var world = new ComponentStorage();
        world.SetComponent(row, new ErrorComponent { IsFatal = false });
        var result = pipeline.Execute(new[] { row }, world, cancellation.Token);
        Assert.Equal(1, scoped.Cleanups);
        if (execution == "fatal")
        {
            Assert.Same(initiating, result.Exception);
            Assert.Equal(initiating.Message, result.ErrorMessage);
            Assert.False(result.IsCancellation);
        }
        else if (execution == "cancel") Assert.True(result.IsCancellation);
        else if (cleanup == "fail") Assert.Same(cleanupError, result.Exception);
        else Assert.True(result.IsSuccess);
        Assert.Equal(result.IsSuccess ? 1 : 0, later.Calls);
    }

    [Fact]
    public void UnrelatedCancellationExceptionRemainsFatalEvenWhenCallerCancels()
    {
        using var cancellation = new CancellationTokenSource();
        var unrelated = new OperationCanceledException("unrelated subsystem cancellation");
        var system = new DelegateScope(() => { cancellation.Cancel(); throw unrelated; }, () => Result.Success());
        var result = new Pipe("unrelated").AddSystem(system)
            .Execute(new[] { new Entity() }, new ComponentStorage(), new TestLogger(true), cancellation.Token);
        Assert.False(result.IsCancellation);
        Assert.Same(unrelated, result.Exception);
        Assert.Equal(1, system.Cleanups);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FirstObservedWorkerFailureWinsRegardlessOfEntityOrder(bool cancelSibling)
    {
        var laterEntity = new Entity();
        var firstEntity = new Entity();
        var expected = new IOException("first observed failure");
        using var cancellation = new CancellationTokenSource();
        using var recorded = new ManualResetEventSlim();
        var storage = new SignallingStorage(recorded, firstEntity);
        var worker = new OrderedWorker(firstEntity, expected, recorded, cancellation, cancelSibling);
        var result = new Pipe("workers").AddSystem(worker)
            .Execute(new[] { laterEntity, firstEntity }, storage, new TestLogger(true), cancellation.Token);
        Assert.False(result.IsCancellation);
        Assert.Same(expected, result.Exception);
        Assert.Equal(expected.Message, result.ErrorMessage);
        Assert.Same(expected, storage.GetComponent<ErrorComponent>(firstEntity)!.Exception);
        Assert.Equal(1, worker.Cleanups);
    }

    private sealed class OrderedWorker(Entity first, Exception error, ManualResetEventSlim recorded,
        CancellationTokenSource cancellation, bool cancelSibling) : ISystem, IExecutionScopedSystem
    {
        public int Cleanups;
        public Result BeginExecution(IEnumerable<Entity> entities, IComponentStorage storage) => Result.Success();
        public Result Execute(Entity entity, IComponentStorage storage)
        {
            if (entity == first) return Result.Fail(error.Message, error);
            if (!recorded.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("First worker never completed.");
            if (cancelSibling) { cancellation.Cancel(); cancellation.Token.ThrowIfCancellationRequested(); }
            return Result.Fail("second observed failure", new IOException("second observed failure"));
        }
        public Result EndExecution(IEnumerable<Entity> entities, IComponentStorage storage) { Cleanups++; return Result.Success(); }
    }

    private sealed class DelegateScope(Func<Result> execute, Func<Result> cleanup) : ISystem, IExecutionScopedSystem
    {
        public int Calls, Cleanups;
        public Result BeginExecution(IEnumerable<Entity> entities, IComponentStorage storage) => Result.Success();
        public Result Execute(Entity entity, IComponentStorage storage) { Calls++; return execute(); }
        public Result EndExecution(IEnumerable<Entity> entities, IComponentStorage storage) { Cleanups++; return cleanup(); }
    }

    private sealed class TestLogger(bool throws) : IPipelineLogger, IProgressLogger
    {
        public void Log(string value) { if (throws) throw new IOException("logger failed"); }
        public void ReportProgress(string value) => Log(value);
        public void Dispose() { }
    }

    // This observer delegates every operation to real component storage. Its
    // signal orders worker failures after the pipe records the first result.
    private sealed class SignallingStorage(ManualResetEventSlim recorded, Entity first) : IComponentStorage
    {
        private readonly ComponentStorage _storage = new();
        public T GetComponent<T>(Entity e) where T : class, IComponent => _storage.GetComponent<T>(e);
        public void SetComponent<T>(Entity e, T value) where T : class, IComponent
        { _storage.SetComponent(e, value); if (e == first && value is ErrorComponent) recorded.Set(); }
        public bool HasComponent<T>(Entity e) where T : class, IComponent => _storage.HasComponent<T>(e);
        public List<Entity> GetAllEntities() => _storage.GetAllEntities();
        public IReadOnlyList<Entity> Query<T>() where T : class, IComponent => _storage.Query<T>();
        public IReadOnlyList<Entity> Query<T1, T2>() where T1 : class, IComponent where T2 : class, IComponent => _storage.Query<T1, T2>();
        public IReadOnlyList<Entity> Query<T1, T2, T3>() where T1 : class, IComponent where T2 : class, IComponent where T3 : class, IComponent => _storage.Query<T1, T2, T3>();
    }
}
