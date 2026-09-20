using DOPipeline.Components;

namespace DifferentialBackup.Components;

/// <summary>
/// The durable phase of an operation.  The phase is data owned by the world;
/// hosts and systems must not infer it from private flags.
/// </summary>
public enum OperationPhase
{
    Initializing,
    InitialPass,
    RetryWaiting,
    RetryPass,
    PreparingPublication,
    PublishedPendingState,
    Finalizing,
    Finished
}

public enum FileWorkState
{
    Discovered,
    ReadyToCapture,
    Deferred,
    Unchanged,
    Captured,
    Omitted
}

public enum DirectoryWorkState
{
    Pending,
    Enumerated,
    Deferred,
    Omitted
}

public enum RestoreEntryState
{
    Pending,
    Restored,
    Deferred,
    Omitted
}

public enum OperationOutcome
{
    Success,
    IncompleteBackup,
    Warnings,
    Failed,
    Cancelled
}

public enum StateCommitStep
{
    NotRequired,
    Pending,
    HashesSaved,
    DatesSaved,
    Complete
}

public enum AttemptDisposition
{
    Succeeded,
    Deferred,
    Omitted,
    Failed
}

public sealed class OperationComponent : IComponent
{
    public Guid RunId { get; set; } = Guid.NewGuid();
    public string OperationType { get; set; } = "backup";
    public string SourceDirectory { get; set; } = string.Empty;
    public string BackupDestination { get; set; } = string.Empty;
    public string RestoreDestination { get; set; } = string.Empty;
    public OperationPhase Phase { get; set; } = OperationPhase.Initializing;
    public int ActivePass { get; set; }
    public DateTime? BackupDate { get; set; }
    public bool InitializationSucceeded { get; set; }
}

public sealed class OperationWaitComponent : IComponent
{
    public Guid RunId { get; set; }
    public DateTimeOffset WakeAtUtc { get; set; }
    public int Pass { get; set; }
}

public sealed class CancellationSignalComponent : IComponent
{
    public Guid RunId { get; set; }
    public string Reason { get; set; } = "Operation cancelled.";
}

public sealed class FatalFailureComponent : IComponent
{
    public Guid RunId { get; set; }
    public string Stage { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? ExceptionType { get; set; }
    public DateTimeOffset OccurredUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class RetryScheduleComponent : IComponent
{
    public static readonly IReadOnlyList<int> DefaultDelayMinutes = new[] { 1, 1, 2, 3, 5, 8, 13 };

    public Guid RunId { get; set; }
    public IReadOnlyList<int> DelayMinutes { get; set; } = DefaultDelayMinutes;
    public int CompletedRound { get; set; }
    public int ActiveRound { get; set; }
    public bool RoundInProgress { get; set; }
    public DateTimeOffset? NextAttemptUtc { get; set; }
}

public sealed class DirectoryWorkComponent : IComponent
{
    public Guid RunId { get; set; }
    public string StableKey { get; set; } = string.Empty;
    public string NormalizedPath { get; set; } = string.Empty;
    public string DisplayPath { get; set; } = string.Empty;
    public DirectoryWorkState State { get; set; } = DirectoryWorkState.Pending;
    public int RequestedPass { get; set; }
    public int LastAttemptedPass { get; set; } = -1;
    public bool IsRoot { get; set; }
}

public sealed class FileWorkComponent : IComponent
{
    public Guid RunId { get; set; }
    public string StableKey { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public FileWorkState State { get; set; } = FileWorkState.Discovered;
    public int RequestedPass { get; set; }
    public int LastAttemptedPass { get; set; } = -1;
    public int? PartNumber { get; set; }
}

public sealed class FileAttemptResultComponent : IComponent
{
    public Guid RunId { get; set; }
    public Guid AttemptId { get; set; } = Guid.NewGuid();
    public int Pass { get; set; }
    public string Stage { get; set; } = string.Empty;
    public AttemptDisposition Disposition { get; set; }
    public string? Hash { get; set; }
    public long? Length { get; set; }
    public string? ErrorType { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTimeOffset OccurredUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class BackupIssueComponent : IComponent
{
    public Guid RunId { get; set; }
    public string StableKey { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string Stage { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string? ExceptionType { get; set; }
    public string? OriginalMessage { get; set; }
    public int AttemptCount { get; set; }
    public DateTimeOffset FirstOccurrenceUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastOccurrenceUtc { get; set; } = DateTimeOffset.UtcNow;
    public bool Resolved { get; set; }
    public bool Retryable { get; set; }
}

public sealed class PartReceiptComponent : IComponent
{
    public Guid RunId { get; set; }
    public int PartNumber { get; set; }
    public IReadOnlyList<BackupPartFile> Files { get; set; } = Array.Empty<BackupPartFile>();
    public string Fingerprint { get; set; } = string.Empty;
    public long ArchiveLength { get; set; }
    public string ArchiveSha256 { get; set; } = string.Empty;
    public long IndexLength { get; set; }
    public string IndexSha256 { get; set; } = string.Empty;
}

public sealed class RestoreEntryComponent : IComponent
{
    public Guid RunId { get; set; }
    public string ArchiveIdentity { get; set; } = string.Empty;
    public string EntryIdentity { get; set; } = string.Empty;
    public string EntryPath { get; set; } = string.Empty;
    public string TargetPath { get; set; } = string.Empty;
    public long? ExpectedLength { get; set; }
    public string? ExpectedHash { get; set; }
    public RestoreEntryState State { get; set; } = RestoreEntryState.Pending;
    public int RequestedPass { get; set; }
    public int LastAttemptedPass { get; set; } = -1;
}

public sealed class RestoreSuccessComponent : IComponent
{
    public Guid RunId { get; set; }
    public string ArchiveIdentity { get; set; } = string.Empty;
    public string EntryIdentity { get; set; } = string.Empty;
    public string TargetPath { get; set; } = string.Empty;
    public long Length { get; set; }
    public string Hash { get; set; } = string.Empty;
    public DateTimeOffset RecordedUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class PersistenceChangeComponent : IComponent
{
    public Guid RunId { get; set; }
    public long Sequence { get; set; }
    public string ChangeType { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = string.Empty;
}

public sealed class OperationOutcomeComponent : IComponent
{
    public Guid RunId { get; set; }
    public OperationOutcome Outcome { get; set; }
    public int ExitCode { get; set; }
    public int EligibleCount { get; set; }
    public int CapturedCount { get; set; }
    public int UnchangedCount { get; set; }
    public int DeferredCount { get; set; }
    public int OmittedCount { get; set; }
    public int ResolvedIssueCount { get; set; }
    public int UnresolvedIssueCount { get; set; }
    public string ReportPath { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
    public DateTimeOffset CompletedUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class StateCommitComponent : IComponent
{
    public Guid RunId { get; set; }
    public StateCommitStep Step { get; set; } = StateCommitStep.NotRequired;
    public string HashesPath { get; set; } = string.Empty;
    public string DatesPath { get; set; } = string.Empty;
}

public sealed class PartAllocationComponent : IComponent
{
    public Guid RunId { get; set; }
    public int HighestReservedPartNumber { get; set; }
}

public sealed class PublicationReceiptComponent : IComponent
{
    public Guid RunId { get; set; }
    public DateTime BackupDate { get; set; }
    public string WorkingDirectory { get; set; } = string.Empty;
    public string FinalDirectory { get; set; } = string.Empty;
    public string ManifestSha256 { get; set; } = string.Empty;
    public IReadOnlyList<int> PartNumbers { get; set; } = Array.Empty<int>();
}

public sealed class IncompleteBackupComponent : IComponent
{
    public Guid RunId { get; set; }
    public string Reason { get; set; } = "Selected backup archive is incomplete.";
}
