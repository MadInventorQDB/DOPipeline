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

        /// <summary>
        /// Returns a stable snapshot of entities that contain the requested component.
        /// The returned collection is safe for a system to enumerate while later systems
        /// perform structural changes.
        /// </summary>
        IReadOnlyList<Entity> Query<T>() where T : class, IComponent;

        /// <summary>
        /// Returns a stable snapshot of entities that contain both requested components.
        /// </summary>
        IReadOnlyList<Entity> Query<T1, T2>()
            where T1 : class, IComponent
            where T2 : class, IComponent;

        /// <summary>
        /// Returns a stable snapshot of entities that contain all requested components.
        /// </summary>
        IReadOnlyList<Entity> Query<T1, T2, T3>()
            where T1 : class, IComponent
            where T2 : class, IComponent
            where T3 : class, IComponent;
    }
}
