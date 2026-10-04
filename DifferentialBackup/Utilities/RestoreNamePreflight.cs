namespace DifferentialBackup.Utilities;

/// <summary>Checks the destination namespace before any existing target is replaced.</summary>
public static class RestoreNamePreflight
{
    public static void Validate(string root, IEnumerable<(string Path, bool Directory)> targets, string? reportName = null)
    {
        root = DirectoryPath.Normalize(root);
        var nodes = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var target in targets)
        {
            var path = DirectoryPath.Normalize(target.Path);
            Add(path, target.Directory);
            for (var parent = Path.GetDirectoryName(path); parent != null && parent != root; parent = Path.GetDirectoryName(parent))
            {
                if (!DirectoryPath.Contains(root, parent)) throw new InvalidDataException("Restore target escapes its root.");
                Add(parent, true);
            }
        }
        Directory.CreateDirectory(root);
        foreach (var group in nodes.GroupBy(pair => Path.GetDirectoryName(pair.Key)!, StringComparer.Ordinal)
            .OrderBy(group => group.Key.Length))
        {
            RejectLinks(root, group.Key);
            Directory.CreateDirectory(group.Key);
            var scratch = Path.Combine(group.Key, ".db-probe-" + Guid.NewGuid().ToString("N"));
            var owned = new List<string>();
            try
            {
                Directory.CreateDirectory(scratch);
                // Per-directory policies must agree with the probe directory.
                foreach (var pair in new[] { ("A", "a"), ("é", "e\u0301"), ("Å", "A\u030a") })
                    if (Equivalent(group.Key, pair.Item1, pair.Item2) != Equivalent(scratch, pair.Item1, pair.Item2))
                        throw new InvalidDataException("Cannot establish the destination directory's name behavior.");
                var existing = Directory.EnumerateFileSystemEntries(group.Key)
                    .Where(path => path != scratch).ToDictionary(path => Path.GetFileName(path)!, StringComparer.Ordinal);
                foreach (var item in group)
                {
                    var name = Path.GetFileName(item.Key);
                    if (existing.TryGetValue(name, out var path))
                    {
                        if (Directory.Exists(path) != item.Value || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                            throw new InvalidDataException($"Restore name has an incompatible existing target: '{item.Key}'.");
                        existing.Remove(name);
                    }
                    CreateProbe(Path.Combine(scratch, name), owned);
                }
                if (group.Key == root)
                    foreach (var name in new[] { ".differential-restore.json", ".differential-restore.log", ".differential-restore.lock", reportName }
                        .Where(name => name != null))
                    {
                        CreateProbe(Path.Combine(scratch, name!), owned);
                        existing.Remove(name!);
                    }
                // Existing alternate spellings must not be silently overwritten.
                foreach (var name in existing.Keys) CreateProbe(Path.Combine(scratch, name), owned);
            }
            catch (IOException ex) { throw new InvalidDataException($"Restore names collide or cannot be checked in '{group.Key}'.", ex); }
            catch (UnauthorizedAccessException ex) { throw new InvalidDataException($"Restore names cannot be checked in '{group.Key}'.", ex); }
            finally
            {
                foreach (var path in owned) File.Delete(path);
                if (Directory.Exists(scratch)) Directory.Delete(scratch, false);
            }
            foreach (var item in group.Where(pair => pair.Value)) Directory.CreateDirectory(item.Key);
        }

        void Add(string path, bool directory)
        {
            if (nodes.TryGetValue(path, out var previous) && previous != directory)
                throw new InvalidDataException($"Restore file/directory conflict: '{path}'.");
            nodes[path] = directory;
        }
    }

    private static void CreateProbe(string path, List<string> owned)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        owned.Add(path);
    }

    private static bool Equivalent(string parent, string first, string second)
    {
        var prefix = ".db-" + Guid.NewGuid().ToString("N") + "-";
        var path = Path.Combine(parent, prefix + first);
        using (new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
        try { return File.Exists(Path.Combine(parent, prefix + second)); }
        finally { File.Delete(path); }
    }

    public static void RejectLinks(string root, string target)
    {
        var current = DirectoryPath.Normalize(target);
        while (DirectoryPath.Contains(root, current))
        {
            if ((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"Restore target or ancestor is a reparse point (link): '{current}'.");
            if (current == DirectoryPath.Normalize(root)) break;
            current = Path.GetDirectoryName(current)!;
        }
    }
}
