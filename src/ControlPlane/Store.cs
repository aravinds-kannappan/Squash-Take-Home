using Microsoft.Data.Sqlite;
using Squash.Contracts;
using System.Text.Json;

namespace Squash.ControlPlane;

// Single-process store. The monitor also serializes multi-statement state transitions.
public sealed class Store : IDisposable
{
    readonly SqliteConnection db;
    readonly object gate = new();
    public Store(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        db = new SqliteConnection($"Data Source={path}");
        db.Open();
        Run("PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON;");
        Run("""
            CREATE TABLE IF NOT EXISTS devices(id TEXT PRIMARY KEY, machine TEXT UNIQUE NOT NULL, body TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS grants(hash TEXT PRIMARY KEY, machine TEXT NOT NULL, pubkey TEXT NOT NULL, expires TEXT NOT NULL, used INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS jobs(id TEXT PRIMARY KEY, device TEXT NOT NULL, idem TEXT NOT NULL UNIQUE, body TEXT NOT NULL);
            """);
    }
    int Run(string sql, params (string, object?)[] args)
    {
        using var c = db.CreateCommand(); c.CommandText = sql;
        foreach (var (k, v) in args) c.Parameters.AddWithValue(k, v ?? DBNull.Value);
        return c.ExecuteNonQuery();
    }
    List<T> Read<T>(string sql, params (string, object?)[] args)
    {
        using var c = db.CreateCommand(); c.CommandText = sql;
        foreach (var (k, v) in args) c.Parameters.AddWithValue(k, v ?? DBNull.Value);
        using var r = c.ExecuteReader(); var rows = new List<T>();
        while (r.Read()) rows.Add(JsonSerializer.Deserialize<T>(r.GetString(0), Protocol.Json)!);
        return rows;
    }
    void Save(Device d) => Run("INSERT INTO devices VALUES($id,$machine,$body) ON CONFLICT(id) DO UPDATE SET body=$body",
        ("$id", d.Id), ("$machine", d.MachineId), ("$body", JsonSerializer.Serialize(d, Protocol.Json)));
    void Save(Execution j) => Run("UPDATE jobs SET body=$body WHERE id=$id", ("$id", j.Id), ("$body", JsonSerializer.Serialize(j, Protocol.Json)));
    public void AddGrant(string token, GrantRequest r)
    {
        lock (gate) Run("INSERT INTO grants(hash,machine,pubkey,expires) VALUES($hash,$machine,$pubkey,$expires)",
            ("$hash", Protocol.Hash(token)), ("$machine", r.MachineId), ("$pubkey", r.PublicKey),
            ("$expires", DateTimeOffset.UtcNow.AddSeconds(r.TtlSeconds).ToString("O")));
    }
    public Device? Enroll(EnrollRequest r)
    {
        lock (gate)
        {
            if (!Protocol.Verify(r.PublicKey, Protocol.EnrollmentProof(r.Token, r.MachineId, r.PublicKey), r.Signature)) return null;
            using var tx = db.BeginTransaction();
            using var c = db.CreateCommand(); c.Transaction = tx;
            c.CommandText = "UPDATE grants SET used=1 WHERE hash=$hash AND machine=$machine AND pubkey=$pubkey AND used=0 AND expires > $now";
            c.Parameters.AddWithValue("$hash", Protocol.Hash(r.Token)); c.Parameters.AddWithValue("$machine", r.MachineId);
            c.Parameters.AddWithValue("$pubkey", r.PublicKey); c.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            if (c.ExecuteNonQuery() != 1) return null;
            // Explicit, operator-authorized re-enrollment preserves ID but rotates identity.
            c.CommandText = "SELECT id FROM devices WHERE machine=$machine";
            var id = c.ExecuteScalar() as string ?? Guid.NewGuid().ToString("N");
            var d = new Device(id, r.MachineId, r.Hostname, r.PublicKey, false, null);
            c.CommandText = "INSERT INTO devices VALUES($id,$machine,$body) ON CONFLICT(id) DO UPDATE SET body=$body";
            c.Parameters.AddWithValue("$id", id); c.Parameters.AddWithValue("$body", JsonSerializer.Serialize(d, Protocol.Json));
            c.ExecuteNonQuery(); tx.Commit(); return d;
        }
    }
    public List<Device> Devices() { lock (gate) return Read<Device>("SELECT body FROM devices ORDER BY id"); }
    public Device? Device(string id) { lock (gate) return Read<Device>("SELECT body FROM devices WHERE id=$id", ("$id", id)).SingleOrDefault(); }
    public void Seen(string id) { lock (gate) { var d = Device(id); if (d is not null) Save(d with { LastSeen = DateTimeOffset.UtcNow }); } }
    public bool Revoke(string id)
    {
        lock (gate)
        {
            var d = Device(id); if (d is null) return false; Save(d with { Revoked = true });
            foreach (var j in Jobs().Where(x => x.DeviceId == id && !States.Terminal.Contains(x.Status)))
                Complete(j.Id, Failure(j, "revoked", "Device identity was revoked."));
            return true;
        }
    }
    public (Execution Job, bool Created) Create(string device, string idem, ExecutionRequest r, string caller)
    {
        lock (gate)
        {
            var old = Read<Execution>("SELECT body FROM jobs WHERE idem=$idem", ("$idem", caller + ":" + idem)).SingleOrDefault();
            if (old is not null)
            {
                if (old.DeviceId != device || old.Script != r.Script || old.TimeoutSeconds != r.TimeoutSeconds ||
                    (int)(old.DispatchDeadline - old.CreatedAt).TotalSeconds != r.DispatchTimeoutSeconds)
                    throw new InvalidOperationException("Idempotency key already belongs to a different request.");
                return (old, false);
            }
            var now = DateTimeOffset.UtcNow;
            var j = new Execution(Guid.NewGuid().ToString("N"), device, r.Script, Protocol.Hash(r.Script), "queued",
                r.TimeoutSeconds, now, now.AddSeconds(r.DispatchTimeoutSeconds), null, null, null, caller, idem);
            Run("INSERT INTO jobs VALUES($id,$device,$idem,$body)", ("$id", j.Id), ("$device", device),
                ("$idem", caller + ":" + idem), ("$body", JsonSerializer.Serialize(j, Protocol.Json)));
            return (j, true);
        }
    }
    public List<Execution> Jobs() { lock (gate) return Read<Execution>("SELECT body FROM jobs ORDER BY rowid DESC"); }
    public Execution? Job(string id) { lock (gate) return Read<Execution>("SELECT body FROM jobs WHERE id=$id", ("$id", id)).SingleOrDefault(); }
    public Execution? Dispatch(string id)
    {
        lock (gate)
        {
            var j = Job(id); if (j?.Status != "queued" || j.DispatchDeadline <= DateTimeOffset.UtcNow) return null;
            j = j with { Status = "dispatched", StartedAt = DateTimeOffset.UtcNow }; Save(j); return j;
        }
    }
    public void Started(string id, string device)
    {
        lock (gate) { var j = Job(id); if (j?.DeviceId == device && j.Status == "dispatched") Save(j with { Status = "running" }); }
    }
    public bool Complete(string id, ExecutionResult result, string? device = null)
    {
        lock (gate)
        {
            var j = Job(id);
            if (j is null || States.Terminal.Contains(j.Status) || (device is not null && (j.DeviceId != device || j.Status == "queued"))) return false;
            if (result.ScriptSha256 != j.ScriptSha256 || !States.Terminal.Contains(result.Status)) return false;
            Save(j with { Status = result.Status, CompletedAt = DateTimeOffset.UtcNow, Result = result }); return true;
        }
    }
    public static ExecutionResult Failure(Execution j, string status, string error) => new(status, null, "", "", 0, false, false, j.ScriptSha256, error);
    public void Sweep(bool startup = false)
    {
        lock (gate)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var j in Jobs().Where(j => !States.Terminal.Contains(j.Status)))
            {
                if (startup && j.Status != "queued") Complete(j.Id, Failure(j, "interrupted", "Control plane restarted; execution outcome is uncertain. No retry."));
                else if (j.Status == "queued" && j.DispatchDeadline <= now) Complete(j.Id, Failure(j, "offline", "No agent accepted the job before its dispatch deadline."));
                else if (j.StartedAt is { } started && now > started.AddSeconds(j.TimeoutSeconds + 5))
                    Complete(j.Id, Failure(j, "timed_out", "Result deadline exceeded; remote outcome is uncertain."));
            }
        }
    }
    public void Dispose() => db.Dispose();
}
