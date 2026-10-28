using System.Text.Json;
using Microsoft.Data.Sqlite;
using ObstacleTimer.Core;

namespace ObstacleTimer.Desktop;

public sealed class Store : IDisposable
{
    private readonly SqliteConnection db;
    private readonly FileStream instanceLock;
    public string DirectoryPath { get; }
    public Store(string directory)
    {
        DirectoryPath = directory;
        Directory.CreateDirectory(directory);
        try { instanceLock = new FileStream(Path.Combine(directory, "host.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new DomainException("这个数据目录的计时程序已在运行，请回到已打开的窗口"); }
        db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "timing.sqlite"), Mode = SqliteOpenMode.ReadWriteCreate }.ToString());
        db.Open();
        using (var version = db.CreateCommand())
        {
            version.CommandText = "PRAGMA user_version";
            long current = (long)version.ExecuteScalar()!;
            if (current > 3) throw new DomainException("数据由更新版本创建，请使用对应版本程序");
            if (current is 1 or 2)
            {
                using var backup = new SqliteConnection(new SqliteConnectionStringBuilder {
                    DataSource = Path.Combine(directory, "timing.pre-v3-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-ffff") + ".sqlite") }.ToString());
                backup.Open(); db.BackupDatabase(backup);
            }
        }
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON;
            CREATE TABLE IF NOT EXISTS athletes(id TEXT PRIMARY KEY, json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS runs(id TEXT PRIMARY KEY, json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS events(id TEXT PRIMARY KEY, created TEXT NOT NULL, kind TEXT NOT NULL, json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS evidence(id TEXT NOT NULL, kind TEXT NOT NULL, run_id TEXT NOT NULL,
              sha256 TEXT NOT NULL, length INTEGER NOT NULL, path TEXT NOT NULL, PRIMARY KEY(id,kind));
            CREATE TABLE IF NOT EXISTS heats(id TEXT PRIMARY KEY, json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS device_impulses(id TEXT PRIMARY KEY, json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS binding_history(id TEXT PRIMARY KEY, json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS validation_profiles(id TEXT PRIMARY KEY, json TEXT NOT NULL);
            PRAGMA user_version=3;
            """;
        cmd.ExecuteNonQuery();
    }
    public void Load(RaceEngine engine)
    {
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT json FROM athletes ORDER BY rowid";
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) engine.Athletes.Add(JsonSerializer.Deserialize<Athlete>(reader.GetString(0), Wire.Json)!);
        }
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT json FROM runs ORDER BY rowid";
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) engine.Runs.Add(JsonSerializer.Deserialize<RaceRun>(reader.GetString(0), Wire.Json)!);
        }
        LoadRows("heats", engine.Heats); LoadRows("device_impulses", engine.DeviceImpulses); LoadRows("binding_history", engine.BindingHistory);
        LoadRows("validation_profiles", engine.ValidationProfiles);
        engine.RecoverAfterRestart();
    }
    private void LoadRows<T>(string table, List<T> target)
    {
        using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT json FROM " + table + " ORDER BY rowid";
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) target.Add(JsonSerializer.Deserialize<T>(reader.GetString(0), Wire.Json)!);
    }
    public void Save(RaceEngine engine, string kind, object detail)
    {
        using var tx = db.BeginTransaction();
        foreach (var a in engine.Athletes) Upsert("athletes", a.Id, JsonSerializer.Serialize(a, Wire.Json), tx);
        foreach (var r in engine.Runs) Upsert("runs", r.Id, JsonSerializer.Serialize(r, Wire.Json), tx);
        foreach (var h in engine.Heats) Upsert("heats", h.Id, JsonSerializer.Serialize(h, Wire.Json), tx);
        foreach (var i in engine.DeviceImpulses) Upsert("device_impulses", i.EventId, JsonSerializer.Serialize(i, Wire.Json), tx);
        foreach (var b in engine.BindingHistory) Upsert("binding_history", b.Id, JsonSerializer.Serialize(b, Wire.Json), tx);
        foreach (var p in engine.ValidationProfiles) Upsert("validation_profiles", p.Id, JsonSerializer.Serialize(p, Wire.Json), tx);
        Log(kind, detail, tx);
        tx.Commit();
    }
    private void Upsert(string table, string id, string json, SqliteTransaction tx)
    {
        using var cmd = db.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = $"INSERT INTO {table}(id,json) VALUES($id,$json) ON CONFLICT(id) DO UPDATE SET json=excluded.json";
        cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$json", json); cmd.ExecuteNonQuery();
    }
    public void Log(string kind, object detail, SqliteTransaction? tx = null)
    {
        using var cmd = db.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO events VALUES($id,$created,$kind,$json)";
        cmd.Parameters.AddWithValue("$id", Wire.Id()); cmd.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$kind", kind); cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(detail, Wire.Json)); cmd.ExecuteNonQuery();
    }
    public void AddEvidence(string id, string kind, string runId, string hash, long length, string path)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO evidence VALUES($id,$kind,$run,$hash,$length,$path)";
        cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$kind", kind); cmd.Parameters.AddWithValue("$run", runId);
        cmd.Parameters.AddWithValue("$hash", hash); cmd.Parameters.AddWithValue("$length", length); cmd.Parameters.AddWithValue("$path", path); cmd.ExecuteNonQuery();
    }
    public bool HasEvidence(string id, string kind, string runId)
    {
        using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT COUNT(*) FROM evidence WHERE id=$id AND kind=$kind AND run_id=$run";
        cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$kind", kind); cmd.Parameters.AddWithValue("$run", runId);
        return (long)cmd.ExecuteScalar()! > 0;
    }
    public string EvidenceHash(string id,string kind,string owner)
    {
        using var cmd=db.CreateCommand();cmd.CommandText="SELECT sha256 FROM evidence WHERE id=$id AND kind=$kind AND run_id=$owner";
        cmd.Parameters.AddWithValue("$id",id);cmd.Parameters.AddWithValue("$kind",kind);cmd.Parameters.AddWithValue("$owner",owner);
        return cmd.ExecuteScalar() as string ?? "";
    }
    public void Dispose() { db.Dispose(); instanceLock.Dispose(); }
}
