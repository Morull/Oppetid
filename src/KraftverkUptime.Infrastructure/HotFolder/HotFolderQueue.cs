using System.Collections.Concurrent;

namespace KraftverkUptime.Infrastructure.HotFolder;

/// <summary>
/// In-memory kø + historikk-buffer for hot-folder-prosesseringen.
/// Eksponeres til UI via <c>/api/v1/hot-folder/...</c> slik at drifts-leder
/// kan følge med i auto-import-banneren på /data-import.
///
/// Queue = filer som ligger i inbox og venter på prosessering.
/// Recent = siste N prosesserte filer (success eller fail) — ringbuffer.
/// </summary>
public sealed class HotFolderQueue
{
    private const int RecentBufferSize = 50;

    private readonly ConcurrentDictionary<string, HotFolderQueueEntry> _queue = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<HotFolderRecentEntry> _recent = new();

    /// <summary>Markér fil som "ligger i inbox og venter".</summary>
    public void EnqueueDetected(string filePath, long fileSize, DateTimeOffset detectedAt)
    {
        _queue[filePath] = new HotFolderQueueEntry(
            FilePath: filePath,
            FileName: Path.GetFileName(filePath),
            FileSize: fileSize,
            DetectedAtUtc: detectedAt,
            Status: "WAITING");
    }

    /// <summary>Oppdater status til "prosesserer nå".</summary>
    public void MarkProcessing(string filePath)
    {
        if (_queue.TryGetValue(filePath, out var entry))
        {
            _queue[filePath] = entry with { Status = "PROCESSING" };
        }
    }

    /// <summary>
    /// Fjern fra kø + legg til i siste-prosessert-buffer. <paramref name="status"/>
    /// = "OK", "QUARANTINE", "DUPLICATE".
    /// </summary>
    public void Complete(string filePath, string status, string? plantId, string? sourceType,
        string? notes, DateTimeOffset completedAt)
    {
        _queue.TryRemove(filePath, out var queued);

        var fileName = queued?.FileName ?? Path.GetFileName(filePath);
        _recent.Enqueue(new HotFolderRecentEntry(
            FileName: fileName,
            PlantId: plantId,
            SourceType: sourceType,
            Status: status,
            ProcessedAtUtc: completedAt,
            Notes: notes));

        // Klipp ringbuffer
        while (_recent.Count > RecentBufferSize && _recent.TryDequeue(out _)) { }
    }

    public IReadOnlyList<HotFolderQueueEntry> GetQueue()
        => _queue.Values.OrderBy(e => e.DetectedAtUtc).ToList();

    public IReadOnlyList<HotFolderRecentEntry> GetRecent(int limit = 20)
        => _recent.Reverse().Take(limit).ToList();
}

public sealed record HotFolderQueueEntry(
    string FilePath,
    string FileName,
    long FileSize,
    DateTimeOffset DetectedAtUtc,
    string Status); // WAITING / PROCESSING

public sealed record HotFolderRecentEntry(
    string FileName,
    string? PlantId,
    string? SourceType,
    string Status, // OK / QUARANTINE / DUPLICATE
    DateTimeOffset ProcessedAtUtc,
    string? Notes);
