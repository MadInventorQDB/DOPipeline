using DOPipeline.Components;
using System.Collections.Generic;

namespace DifferentialBackup.Components
{
    public class DuplicateFilesComponent : IComponent
    {
        public List<List<string>> Groups { get; set; } = new List<List<string>>();
    }
}
