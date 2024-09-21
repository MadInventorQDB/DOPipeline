using DOPipeline.Pipeline;
using DOPipeline.Systems;
using System.Collections.Generic;

namespace DOPipeline.Builders
{
    public class PipeBuilder
    {
        private readonly List<ISystem> _systems = new();
        private string _name = "Unnamed Pipe";

        public PipeBuilder Named(string name)
        {
            _name = name;
            return this;
        }

        public PipeBuilder AddSystem(ISystem system)
        {
            _systems.Add(system);
            return this;
        }

        public Pipe Build()
        {
            var pipe = new Pipe(_name);
            foreach (var system in _systems)
            {
                pipe.AddSystem(system);
            }
            return pipe;
        }
    }
}