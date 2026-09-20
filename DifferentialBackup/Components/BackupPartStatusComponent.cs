using DOPipeline.Components;

namespace DifferentialBackup.Components
{
    public enum BackupPartState
    {
        Planned,
        Staged,
        Transferred,
        Omitted
    }

    public sealed class BackupPartStatusComponent : IComponent
    {
        public Guid RunId { get; set; }

        public BackupPartState State { get; set; }

        public string LocalArchivePath { get; set; } = string.Empty;

        public string DestinationArchivePath { get; set; } = string.Empty;

        public long ArchiveBytes { get; set; }
    }
}
