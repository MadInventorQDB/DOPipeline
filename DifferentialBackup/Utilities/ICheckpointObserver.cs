namespace DifferentialBackup.Utilities;

/// <summary>
/// Test and crash-recovery seam for durable boundaries. Production uses the
/// no-op implementation; a test host can signal and block at a named point.
/// </summary>
public interface ICheckpointObserver
{
    void Reached(string checkpoint);
}

public sealed class NoOpCheckpointObserver : ICheckpointObserver
{
    public static readonly NoOpCheckpointObserver Instance = new();

    private NoOpCheckpointObserver()
    {
    }

    public void Reached(string checkpoint)
    {
    }
}
