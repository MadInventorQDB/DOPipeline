using System;
using System.Collections.Generic;

namespace DifferentialBackup.Utilities
{
    public class BackupManifest
    {
        public const int CurrentFormatVersion = 3;

        public int FormatVersion { get; set; } = CurrentFormatVersion;

        public string SourceDirectory { get; set; } = string.Empty;

        public DateTime BackupDate { get; set; }

        public string PlanFingerprint { get; set; } = string.Empty;

        public int FileCount { get; set; }

        public long SourceBytes { get; set; }

        public bool IsComplete { get; set; } = true;

        public int UnresolvedIssueCount { get; set; }

        public List<BackupManifestIssue> Issues { get; set; } = new();

        public List<BackupManifestPart> Parts { get; set; } = new();
    }

    public class BackupManifestPart
    {
        public int PartNumber { get; set; }

        public string ArchiveFileName { get; set; } = string.Empty;

        public string Fingerprint { get; set; } = string.Empty;

        public int FileCount { get; set; }

        public long SourceBytes { get; set; }

        public long ArchiveBytes { get; set; }

        public string ArchiveSha256 { get; set; } = string.Empty;
        public string IndexFileName { get; set; } = string.Empty;
        public long IndexBytes { get; set; }
        public string IndexSha256 { get; set; } = string.Empty;
    }

    public class BackupManifestIssue
    {
        public string Path { get; set; } = string.Empty;
        public string Stage { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string? ErrorType { get; set; }
        public string? ErrorMessage { get; set; }
        public int Attempts { get; set; }
    }
}
