using DOPipeline.Components;

namespace DifferentialBackup.Components
{
    public sealed class BackupPartComponent : IComponent
    {
        public int PartNumber { get; init; }

        public DateTime BackupDate { get; init; }

        public string ArchiveFileName { get; init; } = string.Empty;

        public string Fingerprint { get; init; } = string.Empty;

        public long SourceBytes { get; init; }

        public IReadOnlyList<BackupPartFile> Files { get; init; } = Array.Empty<BackupPartFile>();
    }

    public sealed record BackupPartFile(
        string SourcePath,
        string EntryName,
        string Hash,
        long Length);
}
