namespace DifferentialBackup.Utilities;

public enum FailureCategory
{
    SourceRead,
    Enumeration,
    SourceChanged,
    UnsupportedObject,
    RestoreTarget,
    OutputUnavailable,
    Integrity,
    Persistence,
    Unexpected
}

public readonly record struct ClassifiedFailure(
    FailureCategory Category,
    bool Retryable,
    bool Fatal,
    bool Omitted);

/// <summary>
/// Central, type based error policy.  It intentionally never inspects localized
/// exception message text; the stage tells us whether an IOException came from
/// a source read or from output storage.
/// </summary>
public static class FailureClassifier
{
    public static ClassifiedFailure Classify(
        string stage,
        string operationType,
        Exception exception)
    {
        if (exception is OperationCanceledException)
        {
            return new ClassifiedFailure(FailureCategory.Unexpected, false, true, false);
        }

        if (stage.Equals("Discovery", StringComparison.OrdinalIgnoreCase) ||
            stage.Equals("Hash", StringComparison.OrdinalIgnoreCase) ||
            stage.Equals("Capture", StringComparison.OrdinalIgnoreCase) ||
            stage.Equals("SourceRead", StringComparison.OrdinalIgnoreCase))
        {
            if (exception is NotSupportedException)
            {
                return new ClassifiedFailure(FailureCategory.UnsupportedObject, false, false, true);
            }

            return new ClassifiedFailure(
                stage.Equals("Discovery", StringComparison.OrdinalIgnoreCase)
                    ? FailureCategory.Enumeration
                    : FailureCategory.SourceRead,
                true,
                false,
                false);
        }

        if (stage.Equals("SourceChanged", StringComparison.OrdinalIgnoreCase))
        {
            return new ClassifiedFailure(FailureCategory.SourceChanged, true, false, false);
        }

        if (stage.Equals("Restore", StringComparison.OrdinalIgnoreCase) ||
            stage.Equals("RestoreTarget", StringComparison.OrdinalIgnoreCase))
        {
            return new ClassifiedFailure(FailureCategory.RestoreTarget, true, false, false);
        }

        if (stage.Equals("Integrity", StringComparison.OrdinalIgnoreCase) ||
            exception is InvalidDataException)
        {
            return new ClassifiedFailure(FailureCategory.Integrity, false, true, false);
        }

        if (stage.Equals("Persistence", StringComparison.OrdinalIgnoreCase) ||
            stage.Equals("State", StringComparison.OrdinalIgnoreCase))
        {
            return new ClassifiedFailure(FailureCategory.Persistence, false, true, false);
        }

        if (stage.Equals("Output", StringComparison.OrdinalIgnoreCase) ||
            stage.Equals("Transfer", StringComparison.OrdinalIgnoreCase) ||
            stage.Equals("Publication", StringComparison.OrdinalIgnoreCase))
        {
            return new ClassifiedFailure(FailureCategory.OutputUnavailable, false, true, false);
        }

        return new ClassifiedFailure(FailureCategory.Unexpected, false, true, false);
    }
}
