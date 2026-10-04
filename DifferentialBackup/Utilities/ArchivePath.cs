namespace DifferentialBackup.Utilities;

/// <summary>Interprets new archive names without changing their Unicode spelling.</summary>
public static class ArchivePath
{
    public static string[] Components(string name, bool portable)
    {
        if (string.IsNullOrEmpty(name) || name.Contains('\0') || name.StartsWith('/') ||
            (!portable && (name.Contains(':') || name.StartsWith('\\'))))
            throw new InvalidDataException($"Unsafe archive path: '{name}'.");
        if (!portable && !OperatingSystem.IsWindows() && name.Contains('\\'))
            throw new InvalidDataException($"Legacy archive has an ambiguous backslash name: '{name}'.");
        var spelling = portable ? name : name.Replace('\\', '/');
        // Exactly one final slash denotes a directory; empty components remain unsafe.
        var parts = (spelling.EndsWith('/') ? spelling[..^1] : spelling).Split('/');
        if (parts.Any(part => part.Length == 0 || part is "." or ".."))
            throw new InvalidDataException($"Unsafe archive path: '{name}'.");
        if (OperatingSystem.IsWindows())
            foreach (var part in parts) ValidateWindowsName(part);
        return parts;
    }

    public static string ToAccessPath(string root, string name, bool portable)
    {
        var parts = Components(name, portable);
        return DirectoryPath.FromRelative(root, Path.Combine(parts));
    }

    private static void ValidateWindowsName(string name)
    {
        if (name.Any(character => character < 32 || "<>:\"/\\|?*".Contains(character)) ||
            name.EndsWith('.') || name.EndsWith(' '))
            throw new InvalidDataException($"Windows cannot represent archive name '{name}'.");
        var stem = name.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
            (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) &&
             "123456789¹²³".Contains(stem[3])))
            throw new InvalidDataException($"Windows reserves archive name '{name}'.");
    }
}
