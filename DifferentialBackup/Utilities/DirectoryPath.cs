namespace DifferentialBackup.Utilities;

/// <summary>Root-preserving operations on native directory paths.</summary>
public static class DirectoryPath
{
    public static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    public static bool Equals(string left, string right) =>
        string.Equals(Normalize(left), Normalize(right), StringComparison.Ordinal);

    public static string WithSeparator(string path)
    {
        var full = Normalize(path);
        return Path.EndsInDirectorySeparator(full) ? full : full + Path.DirectorySeparatorChar;
    }

    public static bool Contains(string parent, string child) =>
        Equals(parent, child) || Normalize(child).StartsWith(WithSeparator(parent), StringComparison.Ordinal);

    public static string FromRelative(string root, string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Contains('\0'))
            throw new InvalidDataException("Expected a relative path.");
        var result = Path.GetFullPath(Path.Combine(root, relative));
        if (!Contains(root, result)) throw new InvalidDataException("Relative path escapes its root.");
        return result;
    }
}
