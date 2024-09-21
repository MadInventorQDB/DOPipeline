
using Xunit;
using DOPipeline.Entities;
using System;

namespace DOPipeline.Test.Entities
{
    public class EntityTests
    {
        [Fact]
        public void NewEntity_HasUniqueId()
        {
            // Act
            var entity = new Entity();

            // Assert
            Assert.NotEqual(Guid.Empty, entity.Id);
        }

        [Fact]
        public void MultipleEntities_HaveUniqueIds()
        {
            // Act
            var entity1 = new Entity();
            var entity2 = new Entity();

            // Assert
            Assert.NotEqual(entity1.Id, entity2.Id);
        }
    }
}
