using System;
using System.Collections.Generic;

namespace DifferentialBackup.Utilities
{
    public class BackupManifest
    {
        public string SourceDirectory { get; set; } = string.Empty;
        public string BackupDestination { get; set; } = string.Empty;
        public DateTime BackupDate { get; set; }
        public string PartialArchiveName { get; set; } = string.Empty;
        public string StagingArchivePath { get; set; } = string.Empty;
        public List<BackupManifestFile> Files { get; set; } = new();
    }

    public class BackupManifestFile
    {
        public int Index { get; set; }
        public string SourcePath { get; set; } = string.Empty;
        public string EntryName { get; set; } = string.Empty;
        public string Hash { get; set; } = string.Empty;
        public long Length { get; set; }
    }
}
