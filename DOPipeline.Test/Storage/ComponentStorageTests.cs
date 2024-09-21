
using Xunit;
using DOPipeline.Storage;
using DOPipeline.Entities;
using DOPipeline.Components;
using DOPipeline.Test.Components;

namespace DOPipeline.Test.Storage
{
    public class ComponentStorageTests
    {
        [Fact]
        public void CanSetAndGetComponent()
        {
            // Arrange
            var storage = new ComponentStorage();
            var entity = new Entity();
            var component = new ExampleComponent();

            // Act
            storage.SetComponent(entity, component);
            var retrievedComponent = storage.GetComponent<ExampleComponent>(entity);

            // Assert
            Assert.NotNull(retrievedComponent);
            Assert.Equal(component, retrievedComponent);
        }

        [Fact]
        public void GetComponent_ReturnsNullIfComponentNotSet()
        {
            // Arrange
            var storage = new ComponentStorage();
            var entity = new Entity();

            // Act
            var component = storage.GetComponent<ExampleComponent>(entity);

            // Assert
            Assert.Null(component);
        }

        [Fact]
        public void HasComponent_ReturnsTrueWhenComponentIsSet()
        {
            // Arrange
            var storage = new ComponentStorage();
            var entity = new Entity();
            var component = new ExampleComponent();

            // Act
            storage.SetComponent(entity, component);
            var hasComponent = storage.HasComponent<ExampleComponent>(entity);

            // Assert
            Assert.True(hasComponent);
        }

        [Fact]
        public void HasComponent_ReturnsFalseWhenComponentNotSet()
        {
            // Arrange
            var storage = new ComponentStorage();
            var entity = new Entity();

            // Act
            var hasComponent = storage.HasComponent<ExampleComponent>(entity);

            // Assert
            Assert.False(hasComponent);
        }

        [Fact]
        public void ComponentsAreStoredPerEntity()
        {
            // Arrange
            var storage = new ComponentStorage();
            var entity1 = new Entity();
            var entity2 = new Entity();
            var component1 = new ExampleComponent();
            var component2 = new ExampleComponent();

            // Act
            storage.SetComponent(entity1, component1);
            storage.SetComponent(entity2, component2);

            // Assert
            Assert.Equal(component1, storage.GetComponent<ExampleComponent>(entity1));
            Assert.Equal(component2, storage.GetComponent<ExampleComponent>(entity2));
            Assert.NotEqual(storage.GetComponent<ExampleComponent>(entity1), storage.GetComponent<ExampleComponent>(entity2));
        }
    }
}
