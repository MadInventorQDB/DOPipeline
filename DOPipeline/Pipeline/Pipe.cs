using System.Collections.Generic;
using DOPipeline.Components;
using DOPipeline.Entities;
using DOPipeline.Storage;
using DOPipeline.Systems;
using DOPipeline.Utilities;

namespace DOPipeline.Pipeline
{
    public class Pipe
    {
        private readonly List<ISystem> _systems = new();

        public string Name { get; }

        public Pipe(string name)
        {
            Name = name;
        }

        public Pipe AddSystem(ISystem system)
        {
            _systems.Add(system);
            return this;
        }

        public Result Execute(IEnumerable<Entity> entities, IComponentStorage storage)
        {
            foreach (var system in _systems)
            {
                foreach (var entity in entities)
                {
                    var result = system.Execute(entity, storage);
                    if (!result.IsSuccess)
                    {
                        storage.SetComponent(entity, new ErrorComponent
                        {
                            ErrorMessage = result.ErrorMessage
                        });
                    }
                }
            }
            return Result.Success();
        }
    }
}