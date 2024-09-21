using DOPipeline.Entities;
using DOPipeline.Storage;
using DOPipeline.Utilities;

namespace DOPipeline.Systems
{
    public interface ISystem
    {
        Result Execute(Entity entity, IComponentStorage storage);
    }
}