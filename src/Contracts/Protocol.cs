using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Squash.Contracts;

public static class Protocol
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public const int MaxScriptBytes = 32_768;
    public const int MaxOutputBytes = 65_536;
    public const int MaxFrameBytes = 1_048_576;
    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    public static string Token() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    public static string Sign(ECDsa key, string text) => Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(text), HashAlgorithmName.SHA256));
    public static bool Verify(string publicKey, string text, string signature)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
            return key.VerifyData(Encoding.UTF8.GetBytes(text), Convert.FromBase64String(signature), HashAlgorithmName.SHA256);
        }
        catch (Exception e) when (e is CryptographicException or FormatException or ArgumentException) { return false; }
    }
    public static string PublicKey(ECDsa key) => Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    public static string EnrollmentProof(string token, string machineId, string publicKey) => $"enroll\n{token}\n{machineId}\n{publicKey}";
    public static string ConnectionProof(string deviceId, string nonce) => $"connect\n{deviceId}\n{nonce}";
    public static async Task Send(WebSocket socket, Wire message, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, Json);
        await socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, ct);
    }
    public static async Task<Wire?> Receive(WebSocket socket, CancellationToken ct)
    {
        using var stream = new MemoryStream();
        var buffer = new byte[8192];
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType != WebSocketMessageType.Text || stream.Length + result.Count > MaxFrameBytes)
                throw new InvalidDataException("Invalid or oversized protocol message.");
            stream.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return JsonSerializer.Deserialize<Wire>(stream.ToArray(), Json) ?? throw new InvalidDataException("Empty protocol message.");
    }
}

public record Wire(string Type, string? DeviceId = null, string? Nonce = null, string? Signature = null,
    string? Payload = null, string? JobId = null, ExecutionResult? Result = null);
public record JobPayload(string Id, string DeviceId, string Script, string ScriptSha256, int TimeoutSeconds,
    DateTimeOffset DispatchDeadline, string Nonce);
public record ExecutionResult(string Status, int? ExitCode, string Stdout, string Stderr, long DurationMs,
    bool StdoutTruncated, bool StderrTruncated, string ScriptSha256, string? Error = null);
public record GrantRequest(string MachineId, string PublicKey, int TtlSeconds = 600);
public record EnrollRequest(string Token, string MachineId, string PublicKey, string Signature, string Hostname);
public record EnrollResponse(string DeviceId, string ServerPublicKey);
public record ExecutionRequest(string Script, int TimeoutSeconds = 30, int DispatchTimeoutSeconds = 15);
public record Device(string Id, string MachineId, string Hostname, string PublicKey, bool Revoked, DateTimeOffset? LastSeen);
public record Execution(string Id, string DeviceId, string Script, string ScriptSha256, string Status,
    int TimeoutSeconds, DateTimeOffset CreatedAt, DateTimeOffset DispatchDeadline, DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt, ExecutionResult? Result, string Caller, string IdempotencyKey);
public static class States
{
    public static readonly HashSet<string> Terminal = ["succeeded", "failed", "timed_out", "offline", "interrupted", "revoked"];
}
