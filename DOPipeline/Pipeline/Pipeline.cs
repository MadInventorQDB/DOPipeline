using System.Collections.Generic;
using DOPipeline.Entities;
using DOPipeline.Storage;
using DOPipeline.Utilities;

namespace DOPipeline.Pipeline
{
    public class Pipeline
    {
        private readonly List<Pipe> _pipes = new();

        public Pipeline AddPipe(Pipe pipe)
        {
            _pipes.Add(pipe);
            return this;
        }

        public Result Execute(IEnumerable<Entity> entities, IComponentStorage storage)
        {
            var currentEntities = entities.ToList();

            foreach (var pipe in _pipes)
            {
                var result = pipe.Execute(currentEntities, storage);
                if (!result.IsSuccess)
                {
                    return Result.Fail($"Pipeline halted due to failure in pipe '{pipe.Name}': {result.ErrorMessage}");
                }

                // Refresh the list of entities from storage
                currentEntities = storage.GetAllEntities();
            }
            return Result.Success();
        }
    }
}