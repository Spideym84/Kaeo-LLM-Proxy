using LiteDB;
using Kaeo.LlmProxy.Core.Models;
using Serilog;

namespace Kaeo.LlmProxy.Infrastructure;

/// <summary>
/// Central LiteDB application database. Stores application data in collections, including
/// request logs, exceptions, model mappings, instruction sets, and heartbeat counters.
/// </summary>
internal sealed class AppDatabase : IDisposable
{
    private const string RequestCollectionName = "requests";
    private const string ExceptionCollectionName = "exceptions";
    private const string ModelMappingCollectionName = "model_mappings";
    private const string InstructionSetCollectionName = "instruction_sets";
    private const string HeartbeatCollectionName = "heartbeats";

    private readonly string _logDir;
    private readonly string _configuredDbPath;
    private readonly long _fileSizeLimitBytes;
    private readonly Lock _lock = new();

    private LiteDatabase? _db;
    private string _currentDbPath = string.Empty;

    public AppDatabase(LoggingSettings settings)
    {
        _configuredDbPath = settings.GetApplicationDatabasePath();
        _logDir = Path.GetDirectoryName(_configuredDbPath)
            ?? Path.Combine(settings.LogDirectory, "requests");
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

            ILiteCollection<RequestLog> col = _db!.GetCollection<RequestLog>(RequestCollectionName);
            col.Insert(entry);
        }
    }

    public IReadOnlyList<ModelMapping> LoadModelMappings()
    {
        lock (_lock)
        {
            return [.. _db!.GetCollection<ModelMapping>(ModelMappingCollectionName).FindAll()];
        }
    }

    public void SaveModelMappings(IEnumerable<ModelMapping> mappings)
    {
        lock (_lock)
        {
            ILiteCollection<ModelMapping> col = _db!.GetCollection<ModelMapping>(ModelMappingCollectionName);
            col.DeleteAll();
            ModelMapping[] items = [.. mappings];
            if (items.Length > 0)
                col.InsertBulk(items);
        }
    }

    public IReadOnlyList<InstructionSet> LoadInstructionSets()
    {
        lock (_lock)
        {
            return [.. _db!.GetCollection<InstructionSet>(InstructionSetCollectionName).FindAll().OrderBy(i => i.Name)];
        }
    }

    public void SaveInstructionSets(IEnumerable<InstructionSet> instructionSets)
    {
        lock (_lock)
        {
            ILiteCollection<InstructionSet> col = _db!.GetCollection<InstructionSet>(InstructionSetCollectionName);
            col.DeleteAll();
            InstructionSet[] items = [.. instructionSets];
            if (items.Length > 0)
                col.InsertBulk(items);
        }
    }

    public IReadOnlyList<(string Model, long Count, DateTime LastSentUtc)> LoadHeartbeatStats()
    {
        lock (_lock)
        {
            return [.. _db!.GetCollection<PersistedHeartbeat>(HeartbeatCollectionName)
                .FindAll()
                .Select(h => (h.Model, h.Count, h.LastSentUtc))];
        }
    }

    public void UpsertHeartbeat(string model, long count, DateTime lastSentUtc)
    {
        lock (_lock)
        {
            ILiteCollection<PersistedHeartbeat> col = _db!.GetCollection<PersistedHeartbeat>(HeartbeatCollectionName);
            col.Upsert(new PersistedHeartbeat
            {
                Model = model,
                Count = count,
                LastSentUtc = lastSentUtc,
            });
        }
    }

    public void ClearHeartbeats()
    {
        lock (_lock)
        {
            _db!.GetCollection<PersistedHeartbeat>(HeartbeatCollectionName).DeleteAll();
        }
    }

    public void SeedDatabaseBackedSettings(AppSettings settings)
    {
        lock (_lock)
        {
            ILiteCollection<ModelMapping> mappings = _db!.GetCollection<ModelMapping>(ModelMappingCollectionName);
            if (mappings.Count() == 0 && settings.ModelMappings.Count > 0)
                mappings.InsertBulk(settings.ModelMappings);

            ILiteCollection<InstructionSet> instructions = _db!.GetCollection<InstructionSet>(InstructionSetCollectionName);
            if (instructions.Count() == 0 && settings.InstructionSets.Count > 0)
                instructions.InsertBulk(settings.InstructionSets);
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
            ILiteCollection<RequestLog> col = _db!.GetCollection<RequestLog>(RequestCollectionName);
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
            ILiteCollection<RequestLog> col = _db!.GetCollection<RequestLog>(RequestCollectionName);
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
            ILiteCollection<RequestLog> col = _db!.GetCollection<RequestLog>(RequestCollectionName);

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
                Log.Debug("AppDatabase pruned {Count} request entries older than {Cutoff:u}", deleted, cutoff);

            return deleted;
        }
    }

    /// <summary>Returns aggregate stats from the active database file.</summary>
    public (long total, long errors, long promptTokens, long completionTokens) QueryTotals()
    {
        lock (_lock)
        {
            ILiteCollection<RequestLog> col = _db!.GetCollection<RequestLog>(RequestCollectionName);
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
        _currentDbPath = _configuredDbPath;

        var connStr = new ConnectionString(_currentDbPath)
        {
            Connection = ConnectionType.Shared,
        };

        _db = new LiteDatabase(connStr);

        // Ensure an index on Timestamp for fast ordered queries.
        ILiteCollection<RequestLog> col = _db.GetCollection<RequestLog>(RequestCollectionName);
        col.EnsureIndex(r => r.Timestamp);

        // Index exceptions by their auto-id (default) — also index timestamp for browsing.
        ILiteCollection<ExceptionDetail> exCol =
            _db.GetCollection<ExceptionDetail>(ExceptionCollectionName);
        exCol.EnsureIndex(e => e.Timestamp);

        _db.GetCollection<ModelMapping>(ModelMappingCollectionName).EnsureIndex(m => m.ProxyName, unique: true);
        _db.GetCollection<ModelMapping>(ModelMappingCollectionName).EnsureIndex(m => m.ModelName);
        _db.GetCollection<InstructionSet>(InstructionSetCollectionName).EnsureIndex(i => i.Name, unique: true);
        _db.GetCollection<PersistedHeartbeat>(HeartbeatCollectionName).EnsureIndex(h => h.Model, unique: true);

        Log.Debug("AppDatabase opened {Path}", _currentDbPath);
    }

    private void CycleIfNeeded()
    {
        if (!File.Exists(_currentDbPath))
            return;

        long size = new FileInfo(_currentDbPath).Length;
        if (size < _fileSizeLimitBytes)
            return;

        Log.Information("AppDatabase cycling — file size {SizeMb:F1} MB exceeds limit", size / 1024.0 / 1024.0);

        _db?.Dispose();
        _db = null;

        string archive = Path.Combine(_logDir,
            $"{Path.GetFileNameWithoutExtension(_currentDbPath)}_{DateTime.UtcNow:yyyyMMdd_HHmmss}{Path.GetExtension(_currentDbPath)}");
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

internal sealed class PersistedHeartbeat
{
    [BsonId]
    public string Model { get; set; } = string.Empty;

    public long Count { get; set; }

    public DateTime LastSentUtc { get; set; }
}
