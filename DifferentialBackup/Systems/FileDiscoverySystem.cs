using DOPipeline.Entities;
using DOPipeline.Storage;
using DOPipeline.Systems;
using DOPipeline.Utilities;
using DifferentialBackup.Components;

namespace DifferentialBackup.Systems;

/// <summary>
/// Discovers one directory at a time. A directory remains deferred when an
/// iterator fails, while entries yielded before that failure stay in the world.
/// </summary>
public sealed class FileDiscoverySystem : ISystem
{
    private readonly string _sourceDirectory;

    public FileDiscoverySystem(string sourceDirectory)
    {
        _sourceDirectory = Normalize(sourceDirectory);
    }

    public Result Execute(Entity entity, IComponentStorage storage)
    {
        var rootPath = storage.GetComponent<FilePathComponent>(entity)?.FilePath;
        var directory = storage.GetComponent<DirectoryWorkComponent>(entity);
        var operation = storage.GetComponent<OperationComponent>(entity);
        if (operation?.Phase is OperationPhase.Finalizing or
            OperationPhase.PublishedPendingState or OperationPhase.Finished)
        {
            return Result.Success();
        }

        // The first invocation is the operation root. A plain FilePath root is
        // also supported for the duplicate-detection pipeline.
        var firstRootInvocation = directory == null &&
            !storage.Query<DirectoryWorkComponent>()
                .Any(candidate => operation == null ||
                                  storage.GetComponent<DirectoryWorkComponent>(candidate)?.RunId == operation.RunId);
        if (directory == null && !firstRootInvocation &&
            (string.IsNullOrWhiteSpace(rootPath) ||
             !PathsEqual(rootPath!, _sourceDirectory)))
        {
            return Result.Success();
        }

        // Child directory entities do not carry an OperationComponent. Their
        // own durable row is the source of identity and pass information;
        // generating a new GUID here would detach all descendants from the
        // active operation during a retry.
        var runId = operation?.RunId ?? directory?.RunId ?? Guid.Empty;
        if (runId == Guid.Empty)
        {
            runId = Guid.NewGuid();
        }

        var pass = operation?.ActivePass ?? directory?.RequestedPass ?? 0;

        if (directory == null)
        {
            if (!Directory.Exists(_sourceDirectory))
            {
                return Result.Fail($"Source directory does not exist: '{_sourceDirectory}'.");
            }

            directory = new DirectoryWorkComponent
            {
                RunId = runId,
                StableKey = _sourceDirectory,
                NormalizedPath = _sourceDirectory,
                DisplayPath = rootPath ?? _sourceDirectory,
                RequestedPass = pass,
                IsRoot = true
            };
            storage.SetComponent(entity, directory);
            if (string.IsNullOrWhiteSpace(rootPath))
            {
                storage.SetComponent(entity, new FilePathComponent { FilePath = _sourceDirectory });
            }
        }

        if (directory.LastAttemptedPass >= pass && directory.State != DirectoryWorkState.Deferred)
        {
            return Result.Success();
        }

        directory.LastAttemptedPass = pass;
        var context = new DiscoveryContext(runId, pass, _sourceDirectory, storage);
        if (directory.IsRoot)
        {
            return EnumerateTree(entity, directory, context);
        }

        return EnumerateDirectory(entity, directory, context);
    }

    private Result EnumerateTree(
        Entity rootEntity,
        DirectoryWorkComponent root,
        DiscoveryContext context)
    {
        context.PendingDirectories.Enqueue(rootEntity);
        context.QueuedDirectories.Add(root.StableKey);

        // Pre-queue any existing directories that require an attempt in this pass
        foreach (var (_, dirEntity) in context.DirectoryEntities)
        {
            if (dirEntity == rootEntity)
            {
                continue;
            }

            var dir = context.Storage.GetComponent<DirectoryWorkComponent>(dirEntity);
            if (dir != null && dir.RunId == context.RunId &&
                dir.RequestedPass <= context.Pass && dir.LastAttemptedPass < context.Pass &&
                dir.State is not (DirectoryWorkState.Enumerated or DirectoryWorkState.Omitted) &&
                context.QueuedDirectories.Add(dir.StableKey))
            {
                dir.LastAttemptedPass = context.Pass;
                context.Storage.SetComponent(dirEntity, dir);
                context.PendingDirectories.Enqueue(dirEntity);
            }
        }

        while (context.PendingDirectories.Count > 0)
        {
            var currentEntity = context.PendingDirectories.Dequeue();
            var current = context.Storage.GetComponent<DirectoryWorkComponent>(currentEntity);
            if (current == null)
            {
                continue;
            }

            var result = EnumerateDirectory(currentEntity, current, context);
            if (!result.IsSuccess)
            {
                return result;
            }
        }

        return Result.Success();
    }

    private Result EnumerateDirectory(
        Entity directoryEntity,
        DirectoryWorkComponent directory,
        DiscoveryContext context)
    {
        var path = directory.NormalizedPath;
        var possibleFile = context.Storage.GetComponent<FileWorkComponent>(directoryEntity);
        // An entry whose attributes could not be read is represented with
        // both possible roles until a later pass can identify it.  Once the
        // path resolves, keep only the role that matches the filesystem.
        if (possibleFile != null && File.Exists(path) && !Directory.Exists(path))
        {
            directory.State = DirectoryWorkState.Omitted;
            ResolveIssue(directoryEntity, context.Storage);
            return Result.Success();
        }
        if (possibleFile != null && Directory.Exists(path))
        {
            possibleFile.State = FileWorkState.Omitted;
            context.Storage.SetComponent(directoryEntity, possibleFile);
        }
        if (IsReparsePoint(path))
        {
            directory.State = DirectoryWorkState.Omitted;
            SetIssue(directoryEntity, directory.StableKey, path, "Discovery", "UnsupportedFilesystemObject", null, false, context.Storage);
            return Result.Success();
        }

        if (!Directory.Exists(path))
        {
            directory.State = DirectoryWorkState.Deferred;
            SetIssue(directoryEntity, directory.StableKey, path, "Discovery", "SourceRead", new DirectoryNotFoundException(path), true, context.Storage);
            return Result.Success();
        }

        try
        {
            // Consume the iterator directly instead of Directory.GetFiles with
            // SearchOption.AllDirectories. Entries yielded before an exception
            // remain visible and can be processed by later systems.
            var dirInfo = new DirectoryInfo(path);
            using var enumerator = dirInfo.EnumerateFileSystemInfos().GetEnumerator();
            while (true)
            {
                FileSystemInfo info;
                try
                {
                    if (!enumerator.MoveNext())
                    {
                        break;
                    }

                    info = enumerator.Current;
                }
                catch (Exception ex)
                {
                    directory.State = DirectoryWorkState.Deferred;
                    SetIssue(directoryEntity, directory.StableKey, path, "Discovery", "Enumeration", ex, true, context.Storage);
                    return Result.Success();
                }

                AddEntry(info.FullName, info, context);
            }

            directory.State = DirectoryWorkState.Enumerated;
            ResolveIssue(directoryEntity, context.Storage);
            return Result.Success();
        }
        catch (Exception ex)
        {
            directory.State = DirectoryWorkState.Deferred;
            SetIssue(directoryEntity, directory.StableKey, path, "Discovery", "Enumeration", ex, true, context.Storage);
            return Result.Success();
        }
    }

    private void AddEntry(string entryPath, FileSystemInfo? entryInfo, DiscoveryContext context)
    {
        var normalizedEntryPath = Normalize(entryPath);
        context.FileEntities.TryGetValue(normalizedEntryPath, out var existingFileEntity);
        if (existingFileEntity != null)
        {
            var existing = context.Storage.GetComponent<FileWorkComponent>(existingFileEntity)!;
            // Completed rows are authoritative recovery data. Do not ask the
            // source filesystem about them during discovery; the source may
            // have disappeared after their bytes were safely published.
            if (existing.State is FileWorkState.Captured or
                FileWorkState.Unchanged or FileWorkState.Omitted)
            {
                return;
            }
        }

        FileAttributes attributes;
        try
        {
            attributes = entryInfo?.Attributes ?? File.GetAttributes(entryPath);
        }
        catch (Exception ex)
        {
            var entity = existingFileEntity ??
                FindOrCreateFileEntity(entryPath, context);
            var fileWork = context.Storage.GetComponent<FileWorkComponent>(entity)!;
            fileWork.State = FileWorkState.Deferred;
            fileWork.RequestedPass = context.Pass;
            context.Storage.SetComponent(entity, fileWork);
            SetIssue(entity, entryPath, entryPath, "Discovery", "SourceRead", ex, true, context.Storage);
            return;
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            var entity = FindOrCreateIssueEntity(entryPath, context);
            var isDir = (attributes & FileAttributes.Directory) != 0;
            if (isDir)
            {
                var work = context.Storage.GetComponent<DirectoryWorkComponent>(entity) ?? new DirectoryWorkComponent
                {
                    RunId = context.RunId,
                    StableKey = entryPath,
                    NormalizedPath = entryPath,
                    DisplayPath = entryPath,
                    State = DirectoryWorkState.Omitted,
                    RequestedPass = context.Pass,
                    LastAttemptedPass = context.Pass
                };
                work.State = DirectoryWorkState.Omitted;
                context.Storage.SetComponent(entity, work);
                context.DirectoryEntities[Normalize(entryPath)] = entity;
            }
            else
            {
                var reparseFile = context.Storage.GetComponent<FileWorkComponent>(entity) ?? new FileWorkComponent
                {
                    RunId = context.RunId,
                    StableKey = entryPath,
                    SourcePath = entryPath,
                    RelativePath = Path.GetRelativePath(_sourceDirectory, entryPath),
                    RequestedPass = context.Pass,
                    LastAttemptedPass = context.Pass
                };
                reparseFile.RunId = context.RunId;
                reparseFile.StableKey = entryPath;
                reparseFile.SourcePath = entryPath;
                reparseFile.RelativePath = Path.GetRelativePath(_sourceDirectory, entryPath);
                reparseFile.State = FileWorkState.Omitted;
                reparseFile.RequestedPass = context.Pass;
                reparseFile.LastAttemptedPass = context.Pass;
                context.Storage.SetComponent(entity, reparseFile);
                context.FileEntities[Normalize(entryPath)] = entity;
            }
            SetIssue(entity, entryPath, entryPath, "Discovery", "UnsupportedFilesystemObject", null, false, context.Storage);
            return;
        }

        if ((attributes & FileAttributes.Directory) != 0)
        {
            var dirEntity = FindOrCreateDirectoryEntity(entryPath, context);
            var work = context.Storage.GetComponent<DirectoryWorkComponent>(dirEntity);
            if (work != null && work.RequestedPass <= context.Pass && work.LastAttemptedPass < context.Pass &&
                work.State is not (DirectoryWorkState.Enumerated or DirectoryWorkState.Omitted) &&
                context.QueuedDirectories.Add(work.StableKey))
            {
                work.LastAttemptedPass = context.Pass;
                context.Storage.SetComponent(dirEntity, work);
                context.PendingDirectories.Enqueue(dirEntity);
            }
            return;
        }

        var fileEntity = existingFileEntity ??
            FindOrCreateFileEntity(entryPath, context);
        var file = context.Storage.GetComponent<FileWorkComponent>(fileEntity)!;
        file.RequestedPass = file.RequestedPass == 0 && context.Pass > 0
            ? context.Pass
            : Math.Min(file.RequestedPass, context.Pass);
        file.State = file.State is FileWorkState.Captured or FileWorkState.Unchanged or FileWorkState.Omitted
            ? file.State
            : FileWorkState.Discovered;
        context.Storage.SetComponent(fileEntity, file);
    }

    private static Entity FindOrCreateDirectoryEntity(
        string path,
        DiscoveryContext context)
    {
        var normalized = Normalize(path);
        if (context.DirectoryEntities.TryGetValue(normalized, out var existing))
        {
            return existing;
        }

        var created = new Entity();
        context.Storage.SetComponent(created, new DirectoryWorkComponent
        {
            RunId = context.RunId,
            StableKey = normalized,
            NormalizedPath = normalized,
            DisplayPath = path,
            RequestedPass = 0
        });
        context.Storage.SetComponent(created, new FilePathComponent { FilePath = path });
        context.DirectoryEntities[normalized] = created;
        return created;
    }

    private static Entity FindOrCreateFileEntity(
        string path,
        DiscoveryContext context)
    {
        var normalized = Normalize(path);
        if (context.FileEntities.TryGetValue(normalized, out var existing))
        {
            return existing;
        }

        var created = new Entity();
        var relative = Path.GetRelativePath(context.SourceDirectory, normalized);
        context.Storage.SetComponent(created, new FilePathComponent { FilePath = path });
        context.Storage.SetComponent(created, new FileWorkComponent
        {
            RunId = context.RunId,
            StableKey = normalized,
            SourcePath = normalized,
            RelativePath = relative,
            RequestedPass = 0
        });
        context.FileEntities[normalized] = created;
        return created;
    }

    private static Entity FindOrCreateIssueEntity(string path, DiscoveryContext context)
    {
        var normalized = Normalize(path);
        if (context.IssueEntities.TryGetValue(normalized, out var existing))
        {
            return existing;
        }

        var created = new Entity();
        context.Storage.SetComponent(created, new FilePathComponent { FilePath = path });
        context.IssueEntities[normalized] = created;
        return created;
    }

    private static void SetIssue(
        Entity entity,
        string stableKey,
        string path,
        string stage,
        string category,
        Exception? exception,
        bool retryable,
        IComponentStorage storage)
    {
        var issue = storage.GetComponent<BackupIssueComponent>(entity);
        var now = DateTimeOffset.UtcNow;
        if (issue == null)
        {
            issue = new BackupIssueComponent
            {
                RunId = storage.GetComponent<DirectoryWorkComponent>(entity)?.RunId ??
                    storage.GetComponent<FileWorkComponent>(entity)?.RunId ??
                    storage.Query<OperationComponent>()
                        .Select(candidate => storage.GetComponent<OperationComponent>(candidate)?.RunId)
                        .FirstOrDefault() ??
                    Guid.Empty,
                StableKey = stableKey,
                Path = path,
                Stage = stage,
                Category = category,
                ExceptionType = exception?.GetType().FullName,
                OriginalMessage = exception?.Message,
                AttemptCount = 1,
                FirstOccurrenceUtc = now,
                LastOccurrenceUtc = now,
                Retryable = retryable
            };
        }
        else
        {
            issue.AttemptCount++;
            issue.LastOccurrenceUtc = now;
            issue.ExceptionType ??= exception?.GetType().FullName;
            issue.OriginalMessage ??= exception?.Message;
            issue.Retryable = retryable;
            issue.Resolved = false;
        }

        storage.SetComponent(entity, issue);
    }

    private static void ResolveIssue(Entity entity, IComponentStorage storage)
    {
        var issue = storage.GetComponent<BackupIssueComponent>(entity);
        if (issue != null)
        {
            issue.Resolved = true;
            storage.SetComponent(entity, issue);
        }
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return false;
        }
    }

    private static string Normalize(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full);
        return string.Equals(full, root, StringComparison.OrdinalIgnoreCase)
            ? full
            : full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);

    private sealed class DiscoveryContext
    {
        public Guid RunId { get; }
        public int Pass { get; }
        public string SourceDirectory { get; }
        public IComponentStorage Storage { get; }
        public Dictionary<string, Entity> FileEntities { get; }
        public Dictionary<string, Entity> DirectoryEntities { get; }
        public Dictionary<string, Entity> IssueEntities { get; }
        public Queue<Entity> PendingDirectories { get; }
        public HashSet<string> QueuedDirectories { get; }

        public DiscoveryContext(Guid runId, int pass, string sourceDirectory, IComponentStorage storage)
        {
            RunId = runId;
            Pass = pass;
            SourceDirectory = sourceDirectory;
            Storage = storage;
            FileEntities = new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase);
            DirectoryEntities = new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase);
            IssueEntities = new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase);
            PendingDirectories = new Queue<Entity>();
            QueuedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var entity in storage.Query<FileWorkComponent>())
            {
                var component = storage.GetComponent<FileWorkComponent>(entity);
                if (component != null && component.RunId == runId)
                {
                    FileEntities[Normalize(component.StableKey)] = entity;
                }
            }

            foreach (var entity in storage.Query<DirectoryWorkComponent>())
            {
                var component = storage.GetComponent<DirectoryWorkComponent>(entity);
                if (component != null && component.RunId == runId)
                {
                    DirectoryEntities[Normalize(component.StableKey)] = entity;
                }
            }

            foreach (var entity in storage.Query<BackupIssueComponent>())
            {
                var component = storage.GetComponent<BackupIssueComponent>(entity);
                if (component != null && component.RunId == runId)
                {
                    IssueEntities[Normalize(component.StableKey)] = entity;
                }
            }
        }
    }
}
