using System.Threading;
using DOPipeline.Entities;
using DOPipeline.Storage;
using DOPipeline.Utilities;

namespace DOPipeline.Systems;

/// <summary>
/// Optional entity-set contract for systems that perform bounded asynchronous
/// work and need the pipeline cancellation token forwarded to them.
/// </summary>
public interface ICancellableEntitySetSystem : IEntitySetSystem
{
    Result Execute(
        IReadOnlyList<Entity> entities,
        IComponentStorage storage,
        CancellationToken cancellationToken);
}
