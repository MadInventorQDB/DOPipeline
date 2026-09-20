using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DifferentialBackup.Utilities;

public sealed class RestoreJournalRetrySchedule
{
    public int CompletedRound { get; set; }
    public int ActiveRound { get; set; }
    public bool RoundInProgress { get; set; }
    public DateTimeOffset? NextAttemptUtc { get; set; }
}

public sealed record RestoreJournalSuccess(long Length, string Hash);

public sealed class RestoreJournalEntryRecord
{
    public long Sequence { get; set; }
    public string ArchiveIdentity { get; set; } = string.Empty;
    public string EntryIdentity { get; set; } = string.Empty;
    public long Length { get; set; }
    public string Hash { get; set; } = string.Empty;
    public string Checksum { get; set; } = string.Empty;
}

public sealed class RestoreJournal
{
    public string ArchiveIdentity { get; set; } = string.Empty;
    public Dictionary<string, RestoreJournalSuccess> Successes { get; set; } = new(StringComparer.Ordinal);
    public RestoreJournalRetrySchedule? RetrySchedule { get; set; }

    [JsonIgnore]
    public long LastLogSequence { get; set; }

    public static string GetJournalPath(string restoreDestination) =>
        Path.Combine(restoreDestination, ".differential-restore.json");

    public static string GetLogPath(string restoreDestination) =>
        Path.Combine(restoreDestination, ".differential-restore.log");

    public static string ComputeRecordChecksum(RestoreJournalEntryRecord record)
    {
        var value = $"{record.Sequence}|{record.ArchiveIdentity ?? string.Empty}|{record.EntryIdentity ?? string.Empty}|{record.Length}|{record.Hash ?? string.Empty}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    public static RestoreJournal Load(string restoreDestination, string archiveIdentity = "")
    {
        var jsonPath = GetJournalPath(restoreDestination);
        RestoreJournal journal;

        if (File.Exists(jsonPath))
        {
            try
            {
                var value = JsonSerializer.Deserialize<RestoreJournal>(File.ReadAllText(jsonPath));
                if (value == null)
                {
                    throw new InvalidDataException("Restore journal is empty.");
                }

                journal = string.IsNullOrEmpty(archiveIdentity) || string.Equals(value.ArchiveIdentity, archiveIdentity, StringComparison.Ordinal)
                    ? value
                    : new RestoreJournal { ArchiveIdentity = archiveIdentity };
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException("Restore journal is malformed.", ex);
            }
        }
        else
        {
            journal = new RestoreJournal { ArchiveIdentity = archiveIdentity };
        }

        var logPath = GetLogPath(restoreDestination);
        if (File.Exists(logPath))
        {
            var text = File.ReadAllText(logPath);
            if (!string.IsNullOrWhiteSpace(text))
            {
                var lines = text.Split('\n');
                long expectedSequence = 1;

                for (var index = 0; index < lines.Length; index++)
                {
                    var rawLine = lines[index];
                    var line = rawLine.TrimEnd('\r');
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    RestoreJournalEntryRecord? record;
                    try
                    {
                        record = JsonSerializer.Deserialize<RestoreJournalEntryRecord>(line);
                    }
                    catch (JsonException ex)
                    {
                        var isFinalUnterminatedLine = index == lines.Length - 1 && !text.EndsWith('\n');
                        if (isFinalUnterminatedLine)
                        {
                            TruncateLogAtLastCompleteLine(logPath, text);
                            break;
                        }

                        throw new InvalidDataException("Restore journal log is malformed.", ex);
                    }

                    if (record == null ||
                        record.Sequence != expectedSequence ||
                        string.IsNullOrEmpty(record.Checksum) ||
                        !string.Equals(record.Checksum, ComputeRecordChecksum(record), StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException(
                            $"Restore journal log record {index + 1} has an invalid checksum or sequence: '{logPath}'.");
                    }

                    expectedSequence++;
                    journal.LastLogSequence = record.Sequence;

                    if (string.IsNullOrEmpty(archiveIdentity) || string.Equals(record.ArchiveIdentity, archiveIdentity, StringComparison.Ordinal))
                    {
                        if (string.IsNullOrEmpty(journal.ArchiveIdentity))
                        {
                            journal.ArchiveIdentity = record.ArchiveIdentity;
                        }
                        journal.Successes[record.EntryIdentity] = new RestoreJournalSuccess(record.Length, record.Hash);
                    }
                }
            }
        }

        return journal;
    }

    private static void TruncateLogAtLastCompleteLine(string logPath, string text)
    {
        var lastNewline = text.LastIndexOf('\n');
        var completePrefix = lastNewline >= 0
            ? text[..(lastNewline + 1)]
            : string.Empty;
        var byteLength = Encoding.UTF8.GetByteCount(completePrefix);
        using var stream = new FileStream(
            logPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.Read);
        stream.SetLength(byteLength);
        stream.Flush(flushToDisk: true);
    }

    public static void AppendSuccess(
        string restoreDestination,
        string archiveIdentity,
        string entryIdentity,
        RestoreJournalSuccess success,
        RestoreJournal? journal = null)
    {
        Directory.CreateDirectory(restoreDestination);
        var logPath = GetLogPath(restoreDestination);

        long sequence = 1;
        if (journal != null)
        {
            journal.LastLogSequence++;
            sequence = journal.LastLogSequence;
        }
        else if (File.Exists(logPath))
        {
            var existingJournal = Load(restoreDestination, archiveIdentity);
            sequence = existingJournal.LastLogSequence + 1;
        }

        var record = new RestoreJournalEntryRecord
        {
            Sequence = sequence,
            ArchiveIdentity = archiveIdentity,
            EntryIdentity = entryIdentity,
            Length = success.Length,
            Hash = success.Hash
        };
        record.Checksum = ComputeRecordChecksum(record);

        var line = JsonSerializer.Serialize(record);
        using var stream = new FileStream(logPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        if (stream.Length > 0)
        {
            stream.Position = stream.Length - 1;
            var last = stream.ReadByte();
            if (last != '\n')
            {
                stream.WriteByte((byte)'\n');
            }
        }
        stream.Position = stream.Length;
        using var writer = new StreamWriter(stream);
        writer.WriteLine(line);
        writer.Flush();
        stream.Flush(flushToDisk: true);
    }

    public static void Save(string restoreDestination, RestoreJournal journal)
    {
        Directory.CreateDirectory(restoreDestination);
        var path = GetJournalPath(restoreDestination);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(JsonSerializer.Serialize(journal));
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, true);

            var logPath = GetLogPath(restoreDestination);
            try
            {
                if (File.Exists(logPath))
                {
                    File.Delete(logPath);
                }
                journal.LastLogSequence = 0;
            }
            catch
            {
                // Deletion of compacted log is best effort.
            }
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch
            {
                // Cleanup must not replace the journal-write error.
            }
        }
    }
}
