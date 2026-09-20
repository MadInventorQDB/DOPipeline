using DOPipeline.Components;

namespace DifferentialBackup.Components
{
    public sealed class BackupPartComponent : IComponent
    {
        public Guid RunId { get; set; }

        public int PartNumber { get; set; }

        public DateTime BackupDate { get; set; }

        public string ArchiveFileName { get; set; } = string.Empty;

        public string Fingerprint { get; set; } = string.Empty;

        public long SourceBytes { get; set; }

        public IReadOnlyList<BackupPartFile> Files { get; set; } = Array.Empty<BackupPartFile>();
    }

    public sealed record BackupPartFile(
        string SourcePath,
        string EntryName,
        string Hash,
        long Length);
}
