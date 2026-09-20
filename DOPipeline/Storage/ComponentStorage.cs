using System;
using System.Collections.Generic;
using DOPipeline.Entities;
using DOPipeline.Components;
using System.Collections.Concurrent;

namespace DOPipeline.Storage
{
    public class ComponentStorage : IComponentStorage
    {
        private readonly ConcurrentDictionary<Entity, ConcurrentDictionary<Type, IComponent>> _storage = new();
        private readonly ConcurrentDictionary<Type, ConcurrentDictionary<Entity, byte>> _entitiesByType = new();

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
            var components = _storage.GetOrAdd(
                entity,
                static _ => new ConcurrentDictionary<Type, IComponent>());
            components[typeof(T)] = component;

            var typeEntities = _entitiesByType.GetOrAdd(
                typeof(T),
                static _ => new ConcurrentDictionary<Entity, byte>());
            typeEntities[entity] = 0;
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

        public IReadOnlyList<Entity> Query<T>() where T : class, IComponent
        {
            if (_entitiesByType.TryGetValue(typeof(T), out var typeEntities))
            {
                return new List<Entity>(typeEntities.Keys);
            }
            return Array.Empty<Entity>();
        }

        public IReadOnlyList<Entity> Query<T1, T2>()
            where T1 : class, IComponent
            where T2 : class, IComponent
        {
            if (!_entitiesByType.TryGetValue(typeof(T1), out var set1) ||
                !_entitiesByType.TryGetValue(typeof(T2), out var set2))
            {
                return Array.Empty<Entity>();
            }

            var smaller = set1.Count <= set2.Count ? set1 : set2;
            var larger = ReferenceEquals(smaller, set1) ? set2 : set1;
            var result = new List<Entity>();
            foreach (var entity in smaller.Keys)
            {
                if (larger.ContainsKey(entity))
                {
                    result.Add(entity);
                }
            }
            return result;
        }

        public IReadOnlyList<Entity> Query<T1, T2, T3>()
            where T1 : class, IComponent
            where T2 : class, IComponent
            where T3 : class, IComponent
        {
            if (!_entitiesByType.TryGetValue(typeof(T1), out var set1) ||
                !_entitiesByType.TryGetValue(typeof(T2), out var set2) ||
                !_entitiesByType.TryGetValue(typeof(T3), out var set3))
            {
                return Array.Empty<Entity>();
            }

            var smallest = set1;
            if (set2.Count < smallest.Count) smallest = set2;
            if (set3.Count < smallest.Count) smallest = set3;

            var result = new List<Entity>();
            foreach (var entity in smallest.Keys)
            {
                if ((ReferenceEquals(smallest, set1) || set1.ContainsKey(entity)) &&
                    (ReferenceEquals(smallest, set2) || set2.ContainsKey(entity)) &&
                    (ReferenceEquals(smallest, set3) || set3.ContainsKey(entity)))
                {
                    result.Add(entity);
                }
            }
            return result;
        }
    }
}
