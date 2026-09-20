using System.Threading;
using DOPipeline.Entities;
using DOPipeline.Storage;
using DOPipeline.Utilities;

namespace DOPipeline.Systems
{
    /// <summary>
    /// Optional system contract for systems that perform cancellable work. Existing
    /// systems keep the original ISystem contract and continue to work unchanged.
    /// </summary>
    public interface ICancellableSystem
    {
        Result Execute(Entity entity, IComponentStorage storage, CancellationToken cancellationToken);
    }
}
