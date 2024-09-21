using DOPipeline.Entities;
using DOPipeline.Components;
using System.Collections.Generic;

namespace DOPipeline.Storage
{
    public interface IComponentStorage
    {
        T GetComponent<T>(Entity entity) where T : class, IComponent;
        void SetComponent<T>(Entity entity, T component) where T : class, IComponent;
        bool HasComponent<T>(Entity entity) where T : class, IComponent;
        List<Entity> GetAllEntities();
    }
}
