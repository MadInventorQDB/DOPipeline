using DOPipeline.Entities;
using DOPipeline.Storage;
using DOPipeline.Utilities;
using System.Collections.Generic;

namespace DOPipeline.Systems
{
    public interface IExecutionScopedSystem
    {
        Result BeginExecution(IEnumerable<Entity> entities, IComponentStorage storage);
        Result EndExecution(IEnumerable<Entity> entities, IComponentStorage storage);
    }
}
