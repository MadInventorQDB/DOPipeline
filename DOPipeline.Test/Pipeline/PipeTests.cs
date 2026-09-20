using Xunit;
using DOPipeline.Pipeline;
using DOPipeline.Systems;
using DOPipeline.Entities;
using DOPipeline.Storage;
using DOPipeline.Test.Systems;
using DOPipeline.Test.Components;
using System.Collections.Generic;
using DOPipeline.Components;
using DOPipeline.Logging;
using DOPipeline.Utilities;

namespace DOPipeline.Test.Pipeline
{
    public class PipeTests
    {
        [Fact]
        public void CanAddSystemToPipe()
        {
            // Arrange
            var pipe = new Pipe("Test Pipe");
            var system = new ExampleSystem();

            // Act
            pipe.AddSystem(system);

            // Assert
            var entity = new Entity();
            var storage = new ComponentStorage();
            storage.SetComponent(entity, new ExampleComponent());
            var result = pipe.Execute(new[] { entity }, storage);

            Assert.True(result.IsSuccess);
            Assert.Null(storage.GetComponent<ErrorComponent>(entity));
        }

        [Fact]
        public void Execute_CallsSystemsOnEntities()
        {
            // Arrange
            var pipe = new Pipe("Test Pipe");
            var system = new ExampleSystem();
            pipe.AddSystem(system);
            var entity = new Entity();
            var storage = new ComponentStorage();
            storage.SetComponent(entity, new ExampleComponent());

            // Act
            var result = pipe.Execute(new[] { entity }, storage);

            // Assert
            Assert.True(result.IsSuccess);
            Assert.Null(storage.GetComponent<ErrorComponent>(entity));
        }

        [Fact]
        public void Execute_SetsErrorComponentOnSystemFailure()
        {
            // Arrange
            var pipe = new Pipe("Test Pipe");
            var system = new ExampleSystem();
            pipe.AddSystem(system);
            var entity = new Entity();
            var storage = new ComponentStorage();

            // Act
            var result = pipe.Execute(new[] { entity }, storage);

            // Assert
            Assert.False(result.IsSuccess);
            var errorComponent = storage.GetComponent<ErrorComponent>(entity);
            Assert.NotNull(errorComponent);
            Assert.Equal("Component missing.", errorComponent.ErrorMessage);
            Assert.True(errorComponent.IsFatal);
        }

        [Fact]
        public void Execute_HandlesMultipleSystemsAndFailures()
        {
            // Arrange
            var pipe = new Pipe("Test Pipe");
            var failingSystem = new FailingSystem();
            var exampleSystem = new ExampleSystem();
            pipe.AddSystem(failingSystem);
            pipe.AddSystem(exampleSystem);

            var entity1 = new Entity(); // Has component
            var entity2 = new Entity(); // Missing component

            var storage = new ComponentStorage();
            storage.SetComponent(entity1, new ExampleComponent());

            var entities = new List<Entity> { entity1, entity2 };

            // Act
            var result = pipe.Execute(entities, storage);

            // Assert
            Assert.False(result.IsSuccess);

            // Check entity1: Error from FailingSystem persists
            var error1 = storage.GetComponent<ErrorComponent>(entity1);
            Assert.NotNull(error1);
            Assert.Equal("System failed intentionally.", error1.ErrorMessage);

            // The first failed system is a fatal stage boundary, so the later
            // system is not dispatched.
            var error2 = storage.GetComponent<ErrorComponent>(entity2);
            Assert.NotNull(error2);
            Assert.Equal("System failed intentionally.", error2.ErrorMessage);
            Assert.True(error1!.IsFatal);
            Assert.True(error2.IsFatal);
        }

        [Fact]
        public void Execute_CallsEntitySetSystemOnceWithTheCurrentDataSet()
        {
            var pipe = new Pipe("Set Pipe");
            var system = new CountingEntitySetSystem();
            pipe.AddSystem(system);
            var storage = new ComponentStorage();
            var entities = new[] { new Entity(), new Entity(), new Entity() };

            var result = pipe.Execute(entities, storage);

            Assert.True(result.IsSuccess);
            Assert.Equal(1, system.ExecutionCount);
            Assert.Equal(3, system.EntityCount);
        }

        [Fact]
        public void Execute_StopsWhenEntitySetSystemFails()
        {
            var pipe = new Pipe("Set Pipe");
            pipe.AddSystem(new FailingEntitySetSystem());

            var result = pipe.Execute(new[] { new Entity() }, new ComponentStorage());

            Assert.False(result.IsSuccess);
            Assert.Equal("Set failed.", result.ErrorMessage);
        }

        [Fact]
        public void Execute_LoggerFailureStillRunsScopeCleanup()
        {
            var scoped = new ScopedSystem();
            var pipe = new Pipe("Scope Pipe").AddSystem(scoped);
            var result = pipe.Execute(
                new[] { new Entity() },
                new ComponentStorage(),
                new ThrowingProgressLogger());

            Assert.True(result.IsSuccess);
            Assert.True(scoped.Ended);
        }

        [Fact]
        public void Execute_PreservesFatalResultWhenCleanupCancels()
        {
            using var cancellation = new CancellationTokenSource();
            var system = new FailingScopedSystem(cancellation);
            var pipe = new Pipe("Scope Pipe").AddSystem(system);

            var result = pipe.Execute(
                new[] { new Entity() },
                new ComponentStorage(),
                null,
                cancellation.Token);

            Assert.False(result.IsSuccess);
            Assert.False(result.IsCancellation);
            Assert.Equal("initiating fatal error", result.ErrorMessage);
            Assert.True(system.Ended);
        }

        [Fact]
        public void Execute_PreservesOriginalExceptionAtStageBoundary()
        {
            var expected = new IOException("source failure");
            var pipe = new Pipe("Exception Pipe")
                .AddSystem(new ReturningFailureSystem(expected));

            var result = pipe.Execute(
                new[] { new Entity() },
                new ComponentStorage());

            Assert.False(result.IsSuccess);
            Assert.False(result.IsCancellation);
            Assert.Same(expected, result.Exception);
            Assert.Equal("source failure", result.ErrorMessage);
        }

        private sealed class CountingEntitySetSystem : IEntitySetSystem
        {
            public int ExecutionCount { get; private set; }

            public int EntityCount { get; private set; }

            public Result Execute(IReadOnlyList<Entity> entities, IComponentStorage storage)
            {
                ExecutionCount++;
                EntityCount = entities.Count;
                return Result.Success();
            }

            public Result Execute(Entity entity, IComponentStorage storage)
            {
                return Result.Fail("Entity execution should not be used for a set system.");
            }
        }

        private sealed class FailingEntitySetSystem : IEntitySetSystem
        {
            public Result Execute(IReadOnlyList<Entity> entities, IComponentStorage storage)
            {
                return Result.Fail("Set failed.");
            }

            public Result Execute(Entity entity, IComponentStorage storage)
            {
                return Result.Fail("Entity execution should not be used for a set system.");
            }
        }

        private sealed class ScopedSystem : ISystem, IExecutionScopedSystem
        {
            public bool Ended { get; private set; }

            public Result BeginExecution(IEnumerable<Entity> entities, IComponentStorage storage) => Result.Success();

            public Result Execute(Entity entity, IComponentStorage storage) => Result.Success();

            public Result EndExecution(IEnumerable<Entity> entities, IComponentStorage storage)
            {
                Ended = true;
                return Result.Success();
            }
        }

        private sealed class FailingScopedSystem(CancellationTokenSource cancellation)
            : ISystem, IExecutionScopedSystem
        {
            public bool Ended { get; private set; }

            public Result BeginExecution(IEnumerable<Entity> entities, IComponentStorage storage) => Result.Success();

            public Result Execute(Entity entity, IComponentStorage storage) =>
                Result.Fail("initiating fatal error");

            public Result EndExecution(IEnumerable<Entity> entities, IComponentStorage storage)
            {
                Ended = true;
                cancellation.Cancel();
                return Result.Success();
            }
        }

        private sealed class ThrowingProgressLogger : IPipelineLogger, IProgressLogger
        {
            public void Log(string message)
            {
            }

            public void ReportProgress(string message) => throw new IOException("logger failed");

            public void Dispose()
            {
            }
        }

        private sealed class ReturningFailureSystem(Exception exception) : ISystem
        {
            public Result Execute(Entity entity, IComponentStorage storage) =>
                Result.Fail(exception.Message, exception);
        }
    }
}
