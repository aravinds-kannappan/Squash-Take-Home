using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Squash.Contracts;

namespace Squash.Agent;

public record LedgerEntry(JobPayload Job, ExecutionResult? Result, bool Acknowledged = false);
public sealed class AgentWorker(AgentConfig config, Identity identity, string directory, ILogger<AgentWorker> log) : BackgroundService
{
    readonly SemaphoreSlim sendGate = new(1);
    readonly PowerShellRunner runner = new(Path.Combine(directory, "work"));
    string Ledger(string id) => Path.Combine(directory, "ledger", id + ".json");
    async Task Send(ClientWebSocket ws, Wire wire, CancellationToken ct)
    {
        await sendGate.WaitAsync(ct);
        try { await Protocol.Send(ws, wire, ct); } finally { sendGate.Release(); }
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Directory.CreateDirectory(Path.Combine(directory, "ledger"));
        // A crash between persisting intent and persisting the result is uncertain, never retried.
        foreach (var file in Directory.EnumerateFiles(Path.Combine(directory, "ledger"), "*.json"))
        {
            var entry = JsonSerializer.Deserialize<LedgerEntry>(File.ReadAllText(file), Protocol.Json)!;
            if (entry.Result is null) DurableFile.Write(file, entry with { Result = new ExecutionResult("interrupted", null, "", "", 0, false, false, entry.Job.ScriptSha256, "Agent restarted; execution outcome is uncertain. No retry.") });
        }
        var attempts = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Connect(stoppingToken); attempts = 0; }
            catch (Exception e) when (e is WebSocketException or HttpRequestException or IOException or OperationCanceledException or JsonException)
            { if (!stoppingToken.IsCancellationRequested) log.LogWarning("Agent channel disconnected; reconnecting."); }
            if (!stoppingToken.IsCancellationRequested)
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(attempts++, 5))) + Random.Shared.NextDouble()), stoppingToken);
        }
    }
    async Task Connect(CancellationToken stop)
    {
        using var connection = CancellationTokenSource.CreateLinkedTokenSource(stop);
        using var ws = new ClientWebSocket();
        var uri = new UriBuilder(config.ServerUrl) { Scheme = "wss", Path = "/v1/agent/connect" }.Uri;
        using var handshake = CancellationTokenSource.CreateLinkedTokenSource(stop); handshake.CancelAfter(TimeSpan.FromSeconds(15));
        await ws.ConnectAsync(uri, handshake.Token);
        var challenge = await Protocol.Receive(ws, handshake.Token);
        if (challenge?.Type != "challenge" || challenge.Nonce is null) throw new InvalidDataException("Missing challenge.");
        await Send(ws, new Wire("authenticate", DeviceId: config.DeviceId, Signature: identity.Sign(Protocol.ConnectionProof(config.DeviceId, challenge.Nonce))), handshake.Token);
        if ((await Protocol.Receive(ws, handshake.Token))?.Type != "authenticated") throw new InvalidDataException("Authentication failed.");
        log.LogInformation("Agent connected as {DeviceId}.", config.DeviceId);
        var channel = Channel.CreateBounded<JobPayload>(new BoundedChannelOptions(1) { SingleReader = true, SingleWriter = true });
        var worker = ExecuteJobs(); var heartbeat = Heartbeat();
        try
        {
            // Re-deliver persisted results after a network failure; server completion is idempotent.
            foreach (var file in Directory.EnumerateFiles(Path.Combine(directory, "ledger"), "*.json"))
            {
                var entry = JsonSerializer.Deserialize<LedgerEntry>(File.ReadAllText(file), Protocol.Json)!;
                if (entry.Result is not null && !entry.Acknowledged) await Send(ws, new Wire("result", JobId: entry.Job.Id, Result: entry.Result), connection.Token);
            }
            while (!connection.IsCancellationRequested)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(connection.Token);
                idle.CancelAfter(TimeSpan.FromSeconds(25));
                var message = await Protocol.Receive(ws, idle.Token); if (message is null) break;
                if (message.Type == "pong") continue;
                if (message.Type == "result_ack" && Guid.TryParseExact(message.JobId, "N", out _))
                {
                    var path = Ledger(message.JobId!);
                    if (File.Exists(path))
                    {
                        var saved = JsonSerializer.Deserialize<LedgerEntry>(File.ReadAllText(path), Protocol.Json)!;
                        if (saved.Result is not null) DurableFile.Write(path, saved with { Acknowledged = true });
                    }
                    continue;
                }
                if (message.Type != "execute" || message.Payload is null || message.Signature is null || !Protocol.Verify(config.ServerPublicKey, message.Payload, message.Signature))
                    throw new InvalidDataException("Untrusted dispatch.");
                var job = JsonSerializer.Deserialize<JobPayload>(message.Payload, Protocol.Json) ?? throw new InvalidDataException("Invalid job.");
                if (!Guid.TryParseExact(job.Id, "N", out _) || job.DeviceId != config.DeviceId || job.ScriptSha256 != Protocol.Hash(job.Script) ||
                    System.Text.Encoding.UTF8.GetByteCount(job.Script) > Protocol.MaxScriptBytes || job.TimeoutSeconds is < 1 or > 300)
                    throw new InvalidDataException("Invalid job.");
                if (!channel.Writer.TryWrite(job)) throw new InvalidDataException("Unexpected dispatch concurrency.");
            }
        }
        finally
        {
            connection.Cancel(); channel.Writer.TryComplete(); ws.Abort();
            try { await Task.WhenAll(worker, heartbeat); } catch (Exception e) when (e is OperationCanceledException or WebSocketException or IOException) { }
        }
        async Task Heartbeat()
        {
            try { using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5)); while (await timer.WaitForNextTickAsync(connection.Token)) await Send(ws, new Wire("heartbeat"), connection.Token); }
            catch { connection.Cancel(); ws.Abort(); throw; }
        }
        async Task ExecuteJobs()
        {
            try
            {
                await foreach (var job in channel.Reader.ReadAllAsync(connection.Token))
                {
                    var path = Ledger(job.Id);
                    if (File.Exists(path))
                    {
                        var old = JsonSerializer.Deserialize<LedgerEntry>(File.ReadAllText(path), Protocol.Json)!;
                        if (old.Job.ScriptSha256 != job.ScriptSha256) throw new InvalidDataException("Job identity reused with altered content.");
                        if (old.Result is not null) await Send(ws, new Wire("result", JobId: job.Id, Result: old.Result), connection.Token);
                        continue;
                    }
                    if (job.DispatchDeadline < DateTimeOffset.UtcNow) continue;
                    DurableFile.Write(path, new LedgerEntry(job, null));
                    await Send(ws, new Wire("started", JobId: job.Id), connection.Token);
                    var result = await runner.Run(job, connection.Token);
                    DurableFile.Write(path, new LedgerEntry(job, result));
                    await Send(ws, new Wire("result", JobId: job.Id, Result: result), connection.Token);
                }
            }
            catch { connection.Cancel(); ws.Abort(); throw; }
        }
    }
}
