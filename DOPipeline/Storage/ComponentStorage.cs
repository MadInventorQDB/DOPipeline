using System.Collections.Generic;
using DOPipeline.Entities;
using DOPipeline.Components;

namespace DOPipeline.Storage
{
    public class ComponentStorage : IComponentStorage
    {
        private readonly Dictionary<Entity, Dictionary<Type, IComponent>> _storage = new();

        public T GetComponent<T>(Entity entity) where T : class, IComponent
        {
            if (_storage.TryGetValue(entity, out var components) &&
                components.TryGetValue(typeof(T), out var component))
            {
                return component as T;
            }
            return null;
        }

        public void SetComponent<T>(Entity entity, T component) where T : class, IComponent
        {
            if (!_storage.ContainsKey(entity))
            {
                _storage[entity] = new Dictionary<Type, IComponent>();
            }
            _storage[entity][typeof(T)] = component;
        }

        public bool HasComponent<T>(Entity entity) where T : class, IComponent
        {
            return _storage.TryGetValue(entity, out var components) &&
                   components.ContainsKey(typeof(T));
        }

        public List<Entity> GetAllEntities()
        {
            return new List<Entity>(_storage.Keys);
        }
    }
}
