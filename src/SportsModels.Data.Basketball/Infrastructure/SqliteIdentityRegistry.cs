using Microsoft.Data.Sqlite;
using SportsModels.Data.Basketball.Contracts;

namespace SportsModels.Data.Basketball.Infrastructure;

/// <summary>Append-only identity history. One connection per instance; serialize callers.</summary>
public sealed class SqliteIdentityRegistry : IIdentityRegistry, IDisposable
{
    private readonly SqliteConnection connection;
    private SqliteTransaction? activeTransaction;

    public SqliteIdentityRegistry(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        connection.Open();
        Execute("""
            PRAGMA foreign_keys=ON;
            PRAGMA busy_timeout=10000;
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS registry(id TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS entities(id INTEGER PRIMARY KEY AUTOINCREMENT, type TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS mappings(type TEXT NOT NULL, source TEXT NOT NULL, source_id TEXT NOT NULL,
                entity_id INTEGER NOT NULL REFERENCES entities(id), evidence TEXT NOT NULL, PRIMARY KEY(type,source,source_id));
            CREATE TABLE IF NOT EXISTS history(revision INTEGER PRIMARY KEY AUTOINCREMENT,
                entity_id INTEGER REFERENCES entities(id), operation TEXT NOT NULL, evidence TEXT NOT NULL,
                created_utc TEXT NOT NULL DEFAULT(strftime('%Y-%m-%dT%H:%M:%fZ','now')));
            CREATE TABLE IF NOT EXISTS candidates(type TEXT NOT NULL,source TEXT NOT NULL,source_id TEXT NOT NULL,
                evidence TEXT NOT NULL, PRIMARY KEY(type,source,source_id,evidence));
            CREATE TABLE IF NOT EXISTS event_snapshots(source TEXT NOT NULL,game_id TEXT NOT NULL,snapshot TEXT NOT NULL,
                row_key TEXT NOT NULL,action_id TEXT,fingerprint TEXT NOT NULL,entity_id INTEGER NOT NULL REFERENCES entities(id),
                PRIMARY KEY(source,game_id,snapshot,row_key));
            CREATE INDEX IF NOT EXISTS event_match ON event_snapshots(source,game_id,action_id,fingerprint);
            """);

        using var transaction = connection.BeginTransaction(deferred: false);
        if (Scalar("SELECT id FROM registry", transaction) is null)
        {
            Execute("INSERT INTO registry(id) VALUES($id)", transaction, ("$id", Guid.NewGuid().ToString("N")));
        }
        transaction.Commit();
        RegistryId = (string)Scalar("SELECT id FROM registry")!;
    }

    public string RegistryId { get; }
    public long Revision => Convert.ToInt64(Scalar("SELECT COALESCE(MAX(revision),0) FROM history"));

    public T InTransaction<T>(Func<T> operation)
    {
        if (activeTransaction is not null) { return operation(); }
        using var transaction = connection.BeginTransaction(deferred: false);
        activeTransaction = transaction;
        try
        {
            var result = operation();
            transaction.Commit();
            return result;
        }
        finally { activeTransaction = null; }
    }

    public IdentityResolution Resolve(string entityType, string source, string sourceId, string evidence,
        bool establishedNew = false, long? confirmedEntityId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(evidence);
        using var ownedTransaction = activeTransaction is null ? connection.BeginTransaction(deferred: false) : null;
        var transaction = activeTransaction ?? ownedTransaction!;
        var parameters = new[] { ("$type", (object)entityType), ("$source", (object)source), ("$key", (object)sourceId) };
        var existing = Scalar("SELECT entity_id FROM mappings WHERE type=$type AND source=$source AND source_id=$key", transaction, parameters);
        if (existing is long id)
        {
            if (confirmedEntityId is not null && confirmedEntityId != id)
            {
                throw new InvalidOperationException("Reassignment requires an explicit migration of dependent outputs.");
            }
            ownedTransaction?.Commit();
            return new(id, "Existing", evidence);
        }
        if (!establishedNew && confirmedEntityId is null)
        {
            Execute("INSERT OR IGNORE INTO candidates VALUES($type,$source,$key,$evidence)", transaction,
                [.. parameters, ("$evidence", evidence)]);
            ownedTransaction?.Commit();
            return new(null, "Ambiguous", evidence);
        }
        long allocated;
        if (confirmedEntityId is long confirmed)
        {
            if ((string?)Scalar("SELECT type FROM entities WHERE id=$id", transaction, ("$id", confirmed)) != entityType)
            {
                throw new InvalidOperationException("Confirmed entity is missing or has a different type.");
            }
            allocated = confirmed;
        }
        else { allocated = Allocate(entityType, transaction); }
        Execute("INSERT INTO mappings VALUES($type,$source,$key,$id,$evidence)", transaction,
            [.. parameters, ("$id", allocated), ("$evidence", evidence)]);
        Record(allocated, confirmedEntityId is null ? "Allocate" : "ConfirmedMapping", evidence, transaction);
        ownedTransaction?.Commit();
        return new(allocated, "Confirmed", evidence);
    }

    public IdentityResolution ResolveEvent(string source, string gameSourceId, string snapshotId,
        string rowKey, string? actionId, string fingerprint, bool uniqueActionId)
    {
        using var ownedTransaction = activeTransaction is null ? connection.BeginTransaction(deferred: false) : null;
        var transaction = activeTransaction ?? ownedTransaction!;
        var keys = new[] { ("$source", (object)source), ("$game", (object)gameSourceId), ("$snapshot", (object)snapshotId), ("$row", (object)rowKey) };
        var existing = Scalar("SELECT entity_id FROM event_snapshots WHERE source=$source AND game_id=$game AND snapshot=$snapshot AND row_key=$row", transaction, keys);
        if (existing is long known)
        {
            ownedTransaction?.Commit();
            return new(known, "ExistingSnapshot", "Exact retained snapshot and row.");
        }
        // Reuse requires a unique, unchanged full row and the same action ID, never actionNumber.
        var matches = new List<long>();
        if (uniqueActionId && actionId is not null)
        {
            using var command = Command("SELECT DISTINCT entity_id FROM event_snapshots WHERE source=$source AND game_id=$game AND action_id=$action AND fingerprint=$fingerprint", transaction,
                [.. keys, ("$action", actionId), ("$fingerprint", fingerprint)]);
            using var reader = command.ExecuteReader();
            while (reader.Read()) { matches.Add(reader.GetInt64(0)); }
        }
        var reused = matches.Count == 1;
        var otherSnapshot = Scalar("SELECT 1 FROM event_snapshots WHERE source=$source AND game_id=$game AND snapshot<>$snapshot LIMIT 1", transaction, keys) is not null;
        var id = reused ? matches[0] : Allocate("Event", transaction);
        Execute("INSERT INTO event_snapshots VALUES($source,$game,$snapshot,$row,$action,$fingerprint,$id)", transaction,
            [.. keys, ("$action", actionId ?? (object)DBNull.Value), ("$fingerprint", fingerprint), ("$id", id)]);
        var status = reused ? "UnchangedRow" : otherSnapshot ? "SnapshotScoped" : "InitialSnapshot";
        var evidence = status == "SnapshotScoped" ? "Cross-revision correspondence unresolved; new snapshot-scoped ID." : status;
        Record(id, status, $"{source}/{gameSourceId}/{snapshotId}/{rowKey}: {evidence}", transaction);
        ownedTransaction?.Commit();
        return new(id, status, evidence);
    }

    public void Backup(string destination)
    {
        if (File.Exists(destination)) { throw new IOException("Backup destination already exists."); }
        using var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = destination }.ToString());
        target.Open();
        connection.BackupDatabase(target);
    }

    public void Dispose() => connection.Dispose();

    private long Allocate(string type, SqliteTransaction transaction)
    {
        Execute("INSERT INTO entities(type) VALUES($type)", transaction, ("$type", type));
        return (long)Scalar("SELECT last_insert_rowid()", transaction)!;
    }

    private void Record(long id, string operation, string evidence, SqliteTransaction transaction) =>
        Execute("INSERT INTO history(entity_id,operation,evidence) VALUES($id,$operation,$evidence)", transaction,
            ("$id", id), ("$operation", operation), ("$evidence", evidence));

    private SqliteCommand Command(string sql, SqliteTransaction? transaction, params (string Key, object Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (key, value) in parameters) { command.Parameters.AddWithValue(key, value); }
        return command;
    }

    private object? Scalar(string sql, SqliteTransaction? transaction = null, params (string Key, object Value)[] parameters)
    {
        using var command = Command(sql, transaction, parameters);
        return command.ExecuteScalar();
    }

    private void Execute(string sql, SqliteTransaction? transaction = null, params (string Key, object Value)[] parameters)
    {
        using var command = Command(sql, transaction, parameters);
        command.ExecuteNonQuery();
    }
}
