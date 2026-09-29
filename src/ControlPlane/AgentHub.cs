using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Squash.Contracts;

namespace Squash.ControlPlane;

public sealed class ServerKey : IDisposable
{
    readonly ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    readonly object gate = new();
    public string PublicKey => Protocol.PublicKey(key);
    public ServerKey(string directory)
    {
        Directory.CreateDirectory(directory); var path = Path.Combine(directory, "signing.key");
        if (File.Exists(path)) key.ImportPkcs8PrivateKey(File.ReadAllBytes(path), out _);
        else { File.WriteAllBytes(path, key.ExportPkcs8PrivateKey()); if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
    }
    public string Sign(string text) { lock (gate) return Protocol.Sign(key, text); }
    public void Dispose() => key.Dispose();
}

public sealed class AgentHub(Store store, ServerKey key, ResultRedactor redactor) : BackgroundService
{
    sealed class Session(WebSocket socket)
    {
        public WebSocket Socket { get; } = socket;
        public SemaphoreSlim SendGate { get; } = new(1);
        public DateTimeOffset LastSeen { get; set; } = DateTimeOffset.UtcNow;
        public async Task Send(Wire wire, CancellationToken ct)
        {
            await SendGate.WaitAsync(ct);
            try { await Protocol.Send(Socket, wire, ct); } finally { SendGate.Release(); }
        }
    }
    readonly ConcurrentDictionary<string, Session> sessions = new();
    public bool Online(string id) => sessions.TryGetValue(id, out var s) && s.Socket.State == WebSocketState.Open && s.LastSeen > DateTimeOffset.UtcNow.AddSeconds(-20);
    public void Disconnect(string id) { if (sessions.TryRemove(id, out var s)) s.Socket.Abort(); }
    public async Task Connect(WebSocket socket, CancellationToken ct)
    {
        string? id = null; Session? session = null;
        try
        {
            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(ct); handshake.CancelAfter(TimeSpan.FromSeconds(10));
            var nonce = Protocol.Token(); await Protocol.Send(socket, new Wire("challenge", Nonce: nonce), handshake.Token);
            var auth = await Protocol.Receive(socket, handshake.Token);
            var d = auth?.DeviceId is { } deviceId ? store.Device(deviceId) : null;
            if (auth?.Type != "authenticate" || d is null || d.Revoked || auth.Signature is null ||
                !Protocol.Verify(d.PublicKey, Protocol.ConnectionProof(d.Id, nonce), auth.Signature)) return;
            id = d.Id; session = new Session(socket); Disconnect(id); sessions[id] = session;
            store.Seen(id); await session.Send(new Wire("authenticated"), ct);
            while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct); idle.CancelAfter(TimeSpan.FromSeconds(25));
                var message = await Protocol.Receive(socket, idle.Token); if (message is null) break;
                if (store.Device(id) is not { Revoked: false } current || current.PublicKey != d.PublicKey) break;
                session.LastSeen = DateTimeOffset.UtcNow; store.Seen(id);
                if (message.Type == "heartbeat") await session.Send(new Wire("pong"), idle.Token);
                else if (message.Type == "started" && message.JobId is { } started) store.Started(started, id);
                else if (message.Type == "result" && message.JobId is { } jobId && message.Result is { } result)
                {
                    if (result.Stdout is null || result.Stderr is null || Encoding.UTF8.GetByteCount(result.Stdout) > Protocol.MaxOutputBytes || Encoding.UTF8.GetByteCount(result.Stderr) > Protocol.MaxOutputBytes ||
                        result.Error?.Length > 512 || result.DurationMs < 0 || !new[] { "succeeded", "failed", "timed_out", "interrupted" }.Contains(result.Status))
                        throw new InvalidDataException("Invalid execution result.");
                    store.Complete(jobId, redactor.Clean(result), id);
                    // Acknowledge even a late/replayed result if this is the assigned device.
                    if (store.Job(jobId) is { } saved && saved.DeviceId == id && saved.ScriptSha256 == result.ScriptSha256 && States.Terminal.Contains(saved.Status))
                        await session.Send(new Wire("result_ack", JobId: jobId), idle.Token);
                }
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or IOException or JsonException or ArgumentException) { }
        finally
        {
            if (id is not null && session is not null) ((ICollection<KeyValuePair<string, Session>>)sessions).Remove(new(id, session));
            socket.Abort(); socket.Dispose();
        }
    }
    public async Task Dispatch(Execution job, CancellationToken ct)
    {
        if (!Online(job.DeviceId) || !sessions.TryGetValue(job.DeviceId, out var session)) return;
        // One in-flight job per endpoint bounds resource usage; the durable queue holds the rest.
        if (store.Jobs().Any(j => j.DeviceId == job.DeviceId && j.Status is "dispatched" or "running")) return;
        if (store.Dispatch(job.Id) is not { } active) return;
        var payload = JsonSerializer.Serialize(new JobPayload(active.Id, active.DeviceId, active.Script, active.ScriptSha256,
            active.TimeoutSeconds, active.DispatchDeadline, Protocol.Token()), Protocol.Json);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(2));
            await session.Send(new Wire("execute", Signature: key.Sign(payload), Payload: payload), deadline.Token);
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException)
        { Disconnect(job.DeviceId); /* Persist uncertainty; never auto-reexecute a dispatched script. */ }
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        store.Sweep(startup: true);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            store.Sweep();
            foreach (var job in store.Jobs().Where(j => j.Status == "queued").Reverse()) await Dispatch(job, stoppingToken);
        }
    }
}
