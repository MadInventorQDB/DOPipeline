using DOPipeline.Components;

namespace DifferentialBackup.Components
{
    public enum BackupPartState
    {
        Planned,
        Staged,
        Transferred
    }

    public sealed class BackupPartStatusComponent : IComponent
    {
        public BackupPartState State { get; set; }

        public string LocalArchivePath { get; set; } = string.Empty;

        public string DestinationArchivePath { get; set; } = string.Empty;

        public long ArchiveBytes { get; set; }
    }
}
