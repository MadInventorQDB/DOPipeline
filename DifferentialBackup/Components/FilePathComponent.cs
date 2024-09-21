using DOPipeline.Components;

namespace DifferentialBackup.Components
{
    public class FilePathComponent : IComponent
    {
        public string FilePath { get; set; } = string.Empty;
    }
}
