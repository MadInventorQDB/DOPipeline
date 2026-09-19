using DOPipeline.Components;

namespace DifferentialBackup.Components
{
    public sealed class BackupRunComponent : IComponent
    {
        public DateTime BackupDate { get; init; }

        public string SourceDirectory { get; init; } = string.Empty;

        public string BackupDestination { get; init; } = string.Empty;

        public string StagingDirectory { get; init; } = string.Empty;

        public string WorkingDirectory { get; init; } = string.Empty;

        public string FinalDirectory { get; init; } = string.Empty;

        public string PlanFingerprint { get; init; } = string.Empty;

        public int TotalParts { get; init; }

        public int TotalFiles { get; init; }

        public long SourceBytes { get; init; }
    }
}
