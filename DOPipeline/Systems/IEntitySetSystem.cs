using DOPipeline.Entities;
using DOPipeline.Storage;
using DOPipeline.Utilities;

namespace DOPipeline.Systems
{
    public interface IEntitySetSystem : ISystem
    {
        Result Execute(IReadOnlyList<Entity> entities, IComponentStorage storage);
    }
}
