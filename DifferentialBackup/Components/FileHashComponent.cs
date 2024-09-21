using DOPipeline.Components;

namespace DifferentialBackup.Components
{
    public class FileHashComponent : IComponent
    {
        public string CurrentHash { get; set; } = string.Empty;
        public string PreviousHash { get; set; } = string.Empty;
    }
}
