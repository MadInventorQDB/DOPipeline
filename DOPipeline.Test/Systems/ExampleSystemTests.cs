
using Xunit;
using DOPipeline.Test.Systems;
using DOPipeline.Entities;
using DOPipeline.Storage;
using DOPipeline.Test.Components;
using DOPipeline.Components;
using DOPipeline.Utilities;

namespace DOPipeline.Test.Systems
{
    public class ExampleSystemTests
    {
        [Fact]
        public void Execute_ReturnsSuccessWhenComponentExists()
        {
            var system = new ExampleSystem();
            var entity = new Entity();
            var storage = new ComponentStorage();
            storage.SetComponent(entity, new ExampleComponent());

            var result = system.Execute(entity, storage);

            Assert.True(result.IsSuccess);
            Assert.True(storage.HasComponent<ExampleComponent>(entity));
        }

        [Fact]
        public void Execute_ReturnsFailureWhenComponentMissing()
        {
            var system = new ExampleSystem();
            var entity = new Entity();
            var storage = new ComponentStorage();

            var result = system.Execute(entity, storage);

            Assert.False(result.IsSuccess);
            Assert.Equal("Component missing.", result.ErrorMessage);

            var errorComponent = storage.GetComponent<ErrorComponent>(entity);
            Assert.NotNull(errorComponent);
            Assert.Equal("ExampleComponent not found.", errorComponent.ErrorMessage);
        }
    }
}
