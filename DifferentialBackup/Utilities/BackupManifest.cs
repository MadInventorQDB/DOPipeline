using System;
using System.Collections.Generic;

namespace DifferentialBackup.Utilities
{
    public class BackupManifest
    {
        public const int CurrentFormatVersion = 2;

        public int FormatVersion { get; set; } = CurrentFormatVersion;

        public string SourceDirectory { get; set; } = string.Empty;

        public DateTime BackupDate { get; set; }

        public string PlanFingerprint { get; set; } = string.Empty;

        public int FileCount { get; set; }

        public long SourceBytes { get; set; }

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
    }
}
