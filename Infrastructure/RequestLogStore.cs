using LiteDB;
using Kaeo.LlmProxy.Core.Models;
using Serilog;

namespace Kaeo.LlmProxy.Infrastructure;

/// <summary>
/// Persists <see cref="RequestLog"/> entries to LiteDB — a fast, serverless, embedded
/// binary document database. Automatically archives the current file and opens a fresh
/// one when the file size exceeds the configured limit.
/// </summary>
internal sealed class RequestLogStore : IDisposable
{
    private const string CollectionName = "requests";
    private const string ExceptionCollectionName = "exceptions";

    private readonly string _logDir;
    private readonly long _fileSizeLimitBytes;
    private readonly Lock _lock = new();

    private LiteDatabase? _db;
    private string _currentDbPath = string.Empty;

    public RequestLogStore(LoggingSettings settings)
    {
        _logDir = Path.Combine(settings.LogDirectory, "requests");
        _fileSizeLimitBytes = (long)settings.RequestLogFileSizeLimitMb * 1024 * 1024;

        Directory.CreateDirectory(_logDir);
        OpenDatabase();
    }

    /// <summary>
    /// Inserts a request log entry, cycling the database file if it has grown too large.
    /// If <paramref name="ex"/> is provided, the full exception detail is stored in the
    /// exceptions collection and the generated id is linked back onto <paramref name="entry"/>.
    /// </summary>
    public void Insert(RequestLog entry, Exception? ex = null)
    {
        lock (_lock)
        {
            CycleIfNeeded();

            if (ex is not null)
            {
                ExceptionDetail detail = ExceptionDetail.FromException(ex, entry);
                ILiteCollection<ExceptionDetail> exCol =
                    _db!.GetCollection<ExceptionDetail>(ExceptionCollectionName);
                exCol.Insert(detail);
                entry.ExceptionId = detail.Id;
            }

            ILiteCollection<RequestLog> col = _db!.GetCollection<RequestLog>(CollectionName);
            col.Insert(entry);
        }
    }

    /// <summary>Returns the <see cref="ExceptionDetail"/> linked to a request log, or null.</summary>
    public ExceptionDetail? GetException(int exceptionId)
    {
        lock (_lock)
        {
            ILiteCollection<ExceptionDetail> col =
                _db!.GetCollection<ExceptionDetail>(ExceptionCollectionName);
            return col.FindById(exceptionId);
        }
    }

    /// <summary>
    /// Returns the most recent <paramref name="count"/> log entries from the active database,
    /// newest first.
    /// </summary>
    public IReadOnlyList<RequestLog> QueryRecent(int count)
    {
        lock (_lock)
        {
            ILiteCollection<RequestLog> col = _db!.GetCollection<RequestLog>(CollectionName);
            return [.. col.FindAll().OrderByDescending(r => r.Timestamp).Take(count)];
        }
    }

    /// <summary>
    /// Loads up to <paramref name="count"/> recent entries from the active database into
    /// the supplied list, oldest first (so callers can enqueue them in chronological order).
    /// Used to seed the in-memory queue on startup.
    /// </summary>
    public IReadOnlyList<RequestLog> LoadRecent(int count)
    {
        lock (_lock)
        {
            ILiteCollection<RequestLog> col = _db!.GetCollection<RequestLog>(CollectionName);
            return [.. col.FindAll().OrderByDescending(r => r.Timestamp).Take(count).Reverse()];
        }
    }

    /// <summary>
    /// Deletes all request log entries (and their linked exception records) with a
    /// <see cref="RequestLog.Timestamp"/> older than <paramref name="cutoff"/>.
    /// Returns the number of rows deleted.
    /// </summary>
    public int DeleteOlderThan(DateTime cutoff)
    {
        lock (_lock)
        {
            ILiteCollection<RequestLog> col = _db!.GetCollection<RequestLog>(CollectionName);

            // Collect exception ids that are about to be removed so we can clean those up too.
            List<int> exceptionIds = [.. col
                .Find(r => r.Timestamp < cutoff && r.ExceptionId.HasValue)
                .Select(r => r.ExceptionId!.Value)];

            int deleted = col.DeleteMany(r => r.Timestamp < cutoff);

            if (exceptionIds.Count > 0)
            {
                ILiteCollection<ExceptionDetail> exCol =
                    _db!.GetCollection<ExceptionDetail>(ExceptionCollectionName);
                foreach (int id in exceptionIds)
                    exCol.Delete(id);
            }

            if (deleted > 0)
                Log.Debug("RequestLogStore pruned {Count} entries older than {Cutoff:u}", deleted, cutoff);

            return deleted;
        }
    }

    /// <summary>Returns aggregate stats from the active database file.</summary>
    public (long total, long errors, long promptTokens, long completionTokens) QueryTotals()
    {
        lock (_lock)
        {
            ILiteCollection<RequestLog> col = _db!.GetCollection<RequestLog>(CollectionName);
            IEnumerable<RequestLog> all = col.FindAll();

            long total = 0, errors = 0, prompt = 0, completion = 0;

            foreach (RequestLog r in all)
            {
                total++;
                if (r.Status == RequestStatus.Error) errors++;
                prompt += r.PromptTokens;
                completion += r.CompletionTokens;
            }

            return (total, errors, prompt, completion);
        }
    }

    // ── Internals ────────────────────────────────────────────────────────────

    private void OpenDatabase()
    {
        _currentDbPath = Path.Combine(_logDir, "requests_current.db");

        var connStr = new ConnectionString(_currentDbPath)
        {
            Connection = ConnectionType.Shared,
        };

        _db = new LiteDatabase(connStr);

        // Ensure an index on Timestamp for fast ordered queries.
        ILiteCollection<RequestLog> col = _db.GetCollection<RequestLog>(CollectionName);
        col.EnsureIndex(r => r.Timestamp);

        // Index exceptions by their auto-id (default) — also index timestamp for browsing.
        ILiteCollection<ExceptionDetail> exCol =
            _db.GetCollection<ExceptionDetail>(ExceptionCollectionName);
        exCol.EnsureIndex(e => e.Timestamp);

        Log.Debug("RequestLogStore opened {Path}", _currentDbPath);
    }

    private void CycleIfNeeded()
    {
        if (!File.Exists(_currentDbPath))
            return;

        long size = new FileInfo(_currentDbPath).Length;
        if (size < _fileSizeLimitBytes)
            return;

        Log.Information("RequestLogStore cycling — file size {SizeMb:F1} MB exceeds limit", size / 1024.0 / 1024.0);

        _db?.Dispose();
        _db = null;

        string archive = Path.Combine(_logDir,
            $"requests_{DateTime.UtcNow:yyyyMMdd_HHmmss}.db");
        File.Move(_currentDbPath, archive);

        OpenDatabase();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _db?.Dispose();
            _db = null;
        }
    }
}
