using DOPipeline.Components;

namespace DifferentialBackup.Components
{
    public sealed class BackupRunComponent : IComponent
    {
        public Guid RunId { get; set; }

        public DateTime BackupDate { get; set; }

        public string SourceDirectory { get; set; } = string.Empty;

        public string BackupDestination { get; set; } = string.Empty;

        public string StagingDirectory { get; set; } = string.Empty;

        public string WorkingDirectory { get; set; } = string.Empty;

        public string FinalDirectory { get; set; } = string.Empty;

        public string PlanFingerprint { get; set; } = string.Empty;

        public int TotalParts { get; set; }

        public int TotalFiles { get; set; }

        public long SourceBytes { get; set; }
    }
}
