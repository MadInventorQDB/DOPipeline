using DOPipeline.Components;
using DOPipeline.Entities;
using DOPipeline.Storage;
using DOPipeline.Utilities;
using DOPipeline.Systems;
using DOPipeline.Test.Components;

namespace DOPipeline.Test.Systems
{
    public class ExampleSystem : ISystem
    {
        public Result Execute(Entity entity, IComponentStorage storage)
        {
            var component = storage.GetComponent<ExampleComponent>(entity);
            if (component == null)
            {
                storage.SetComponent(entity, new ErrorComponent
                {
                    ErrorMessage = "ExampleComponent not found."
                });
                return Result.Fail("Component missing.");
            }

            storage.SetComponent(entity, component);
            return Result.Success();
        }
    }
}