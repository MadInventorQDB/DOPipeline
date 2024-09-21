
using Xunit;
using DOPipeline.Test.Systems;
using DOPipeline.Entities;
using DOPipeline.Storage;
using DOPipeline.Utilities;

namespace DOPipeline.Test.Systems
{
    public class FailingSystemTests
    {
        [Fact]
        public void Execute_AlwaysReturnsFailure()
        {
            var system = new FailingSystem();
            var entity = new Entity();
            var storage = new ComponentStorage();

            var result = system.Execute(entity, storage);

            Assert.False(result.IsSuccess);
            Assert.Equal("System failed intentionally.", result.ErrorMessage);
        }
    }
}
