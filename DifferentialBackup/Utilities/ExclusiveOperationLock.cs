namespace DifferentialBackup.Utilities;

/// <summary>Ownership is represented by an open handle, never by lock-file existence.</summary>
public sealed class ExclusiveOperationLock : IDisposable
{
    private readonly FileStream _stream;

    private ExclusiveOperationLock(FileStream stream)
    {
        _stream = stream;
    }

    public static ExclusiveOperationLock Acquire(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        try
        {
            return new ExclusiveOperationLock(new FileStream(
                path,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None));
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException($"Another DifferentialBackup operation owns '{path}'.", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new InvalidOperationException($"Cannot acquire operation lock '{path}'.", ex);
        }
    }

    public void Dispose() => _stream.Dispose();
}
