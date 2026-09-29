using System.Security.Cryptography;
using System.Text;
using Squash.Contracts;
using Squash.ControlPlane;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 100_000);
builder.Services.AddSingleton(s => new Store(Path.Combine(s.GetRequiredService<IConfiguration>()["Rmm:DataDir"] ?? "data", "control.db")));
builder.Services.AddSingleton(s => new ServerKey(s.GetRequiredService<IConfiguration>()["Rmm:DataDir"] ?? "data"));
builder.Services.AddSingleton<AgentHub>();
builder.Services.AddSingleton<ResultRedactor>();
builder.Services.AddHostedService(s => s.GetRequiredService<AgentHub>());
builder.Services.AddOpenApi();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.AddPolicy("bootstrap", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
var app = builder.Build();
var apiKey = app.Configuration["Rmm:ApiKey"] ?? throw new InvalidOperationException("Set Rmm__ApiKey to at least 32 random characters.");
if (apiKey.Length < 32) throw new InvalidOperationException("API key must contain at least 32 characters.");
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(10) });
app.UseRateLimiter();
app.Use(async (context, next) =>
{
    var developmentLoopback = app.Environment.IsDevelopment() && context.Connection.RemoteIpAddress is { } ip && System.Net.IPAddress.IsLoopback(ip);
    if (!context.Request.IsHttps && !developmentLoopback) { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { error = "HTTPS required." }); return; }
    var anonymous = context.Request.Path == "/health" || context.Request.Path == "/v1/agents/enroll" || context.Request.Path == "/v1/agent/connect";
    if (!anonymous)
    {
        var supplied = context.Request.Headers.Authorization.ToString();
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(supplied)), SHA256.HashData(Encoding.UTF8.GetBytes("Bearer " + apiKey))))
        { context.Response.StatusCode = 401; return; }
    }
    try { await next(); }
    catch (Exception e) when (e is BadHttpRequestException or System.Text.Json.JsonException or ArgumentException or FormatException)
    { if (!context.Response.HasStarted) { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { error = "Invalid request." }); } }
});
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapOpenApi();
app.MapPost("/v1/enrollment-grants", (GrantRequest request, Store store) =>
{
    if (string.IsNullOrWhiteSpace(request.MachineId) || request.MachineId.Length > 128 || request.PublicKey is null || request.PublicKey.Length > 512 || request.TtlSeconds is < 30 or > 3600)
        return Results.BadRequest(new { error = "Machine ID, P-256 public key and TTL of 30–3600 seconds required." });
    try { using var k = ECDsa.Create(); k.ImportSubjectPublicKeyInfo(Convert.FromBase64String(request.PublicKey), out _); if (k.KeySize != 256) return Results.BadRequest(); }
    catch (Exception e) when (e is CryptographicException or FormatException) { return Results.BadRequest(); }
    var token = Protocol.Token(); store.AddGrant(token, request);
    return Results.Ok(new { token, expiresInSeconds = request.TtlSeconds });
});
app.MapPost("/v1/agents/enroll", (EnrollRequest request, Store store, ServerKey key, AgentHub hub) =>
{
    if (request.Token?.Length != 64 || request.MachineId is null || request.MachineId.Length > 128 || request.PublicKey is null || request.PublicKey.Length > 512 ||
        request.Signature is null || request.Signature.Length > 256 || string.IsNullOrWhiteSpace(request.Hostname) || request.Hostname.Length > 255) return Results.BadRequest();
    var device = store.Enroll(request); if (device is null) return Results.Unauthorized();
    hub.Disconnect(device.Id); return Results.Ok(new EnrollResponse(device.Id, key.PublicKey));
}).RequireRateLimiting("bootstrap");
app.MapGet("/v1/devices", (Store s, AgentHub h) => s.Devices().Select(d => new { d.Id, d.Hostname, d.MachineId, d.Revoked, d.LastSeen, online = h.Online(d.Id) }));
app.MapGet("/v1/devices/{id}", (string id, Store s, AgentHub h) => s.Device(id) is { } d ? Results.Ok(new { d.Id, d.Hostname, d.MachineId, d.Revoked, d.LastSeen, online = h.Online(id) }) : Results.NotFound());
app.MapPost("/v1/devices/{id}/revoke", (string id, Store s, AgentHub h) => { if (!s.Revoke(id)) return Results.NotFound(); h.Disconnect(id); return Results.NoContent(); });
app.MapPost("/v1/devices/{id}/executions", (string id, ExecutionRequest request, HttpContext context, Store store) =>
{
    if (store.Device(id) is not { Revoked: false }) return Results.NotFound();
    var idem = context.Request.Headers["Idempotency-Key"].ToString();
    if (idem.Length is < 1 or > 128 || string.IsNullOrWhiteSpace(request.Script) || request.Script.Contains(apiKey, StringComparison.Ordinal) || Encoding.UTF8.GetByteCount(request.Script) > Protocol.MaxScriptBytes ||
        request.TimeoutSeconds is < 1 or > 300 || request.DispatchTimeoutSeconds is < 1 or > 300)
        return Results.BadRequest(new { error = "Require Idempotency-Key, script ≤32768 UTF-8 bytes, and timeouts of 1–300 seconds." });
    try { var (job, created) = store.Create(id, idem, request, "operator"); return created ? Results.Accepted($"/v1/executions/{job.Id}", job) : Results.Ok(job); }
    catch (InvalidOperationException) { return Results.Conflict(new { error = "Idempotency key already used for a different request." }); }
});
app.MapGet("/v1/executions/{id}", (string id, Store s) => s.Job(id) is { } job ? Results.Ok(job) : Results.NotFound());
app.MapGet("/v1/executions", (Store s, string? deviceId, int? limit) => s.Jobs().Where(j => deviceId is null || j.DeviceId == deviceId).Take(Math.Clamp(limit ?? 50, 1, 200)));
app.Map("/v1/agent/connect", async (HttpContext context, AgentHub hub) =>
{
    if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
    await hub.Connect(await context.WebSockets.AcceptWebSocketAsync(), context.RequestAborted);
}).RequireRateLimiting("bootstrap");
app.Run();
public partial class Program { }
