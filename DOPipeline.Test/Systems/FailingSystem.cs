using DOPipeline.Entities;
using DOPipeline.Storage;
using DOPipeline.Systems;
using DOPipeline.Utilities;

namespace DOPipeline.Test.Systems
{
    public class FailingSystem : ISystem
    {
        public Result Execute(Entity entity, IComponentStorage storage)
        {
            return Result.Fail("System failed intentionally.");
        }
    }
}
