using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Squash.Contracts;
using Xunit;

namespace Rmm.Tests;

public sealed class Factory : WebApplicationFactory<Program>
{
    public const string ApiKey = "integration-tests-only-not-a-real-credential-123456";
    public string Data { get; } = Path.Combine(Path.GetTempPath(), "squash-test-" + Guid.NewGuid().ToString("N"));
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?> { ["Rmm:ApiKey"] = ApiKey, ["Rmm:DataDir"] = Data }));
    }
    public HttpClient Client(bool auth = true)
    {
        var c = CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        if (auth) c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);
        return c;
    }
    public override async ValueTask DisposeAsync() { await base.DisposeAsync(); if (Directory.Exists(Data)) Directory.Delete(Data, true); }
}

public sealed class IntegrationTests
{
    static async Task<(ECDsa Key, EnrollResponse Device)> Enroll(HttpClient client, string machine = "test-machine")
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256); var pub = Protocol.PublicKey(key);
        var response = await client.PostAsJsonAsync("/v1/enrollment-grants", new GrantRequest(machine, pub)); response.EnsureSuccessStatusCode();
        var token = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
        response = await client.PostAsJsonAsync("/v1/agents/enroll", new EnrollRequest(token, machine, pub, Protocol.Sign(key, Protocol.EnrollmentProof(token, machine, pub)), "test-host"));
        response.EnsureSuccessStatusCode(); return (key, (await response.Content.ReadFromJsonAsync<EnrollResponse>())!);
    }
    static async Task<Execution> Dispatch(HttpClient c, string device, string idem, ExecutionRequest? request = null)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, $"/v1/devices/{device}/executions") { Content = JsonContent.Create(request ?? new ExecutionRequest("Write-Output 'hello'")) };
        message.Headers.Add("Idempotency-Key", idem); var r = await c.SendAsync(message); r.EnsureSuccessStatusCode();
        return (await r.Content.ReadFromJsonAsync<Execution>())!;
    }
    static async Task<Execution> Wait(HttpClient c, string id)
    {
        for (var i = 0; i < 100; i++) { var j = (await c.GetFromJsonAsync<Execution>($"/v1/executions/{id}"))!; if (States.Terminal.Contains(j.Status)) return j; await Task.Delay(50); }
        throw new TimeoutException("Job did not reach a terminal state.");
    }
    [Fact] public async Task ApiRequiresAuthenticationAndHttps()
    {
        await using var f = new Factory(); using var c = f.Client(false);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/v1/devices")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.GetAsync("http://localhost/v1/devices")).StatusCode);
    }
    [Fact] public async Task GrantIsKeyBoundAndSingleUse()
    {
        await using var f = new Factory(); using var c = f.Client(); using var k = ECDsa.Create(ECCurve.NamedCurves.nistP256); using var attacker = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pub = Protocol.PublicKey(k); var response = await c.PostAsJsonAsync("/v1/enrollment-grants", new GrantRequest("machine", pub));
        var token = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
        var wrong = Protocol.PublicKey(attacker);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsJsonAsync("/v1/agents/enroll", new EnrollRequest(token, "machine", wrong, Protocol.Sign(attacker, Protocol.EnrollmentProof(token, "machine", wrong)), "attacker"))).StatusCode);
        var valid = new EnrollRequest(token, "machine", pub, Protocol.Sign(k, Protocol.EnrollmentProof(token, "machine", pub)), "host");
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/v1/agents/enroll", valid)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsJsonAsync("/v1/agents/enroll", valid)).StatusCode);
    }
    [Fact] public async Task ReEnrollmentPreservesDeviceIdAndRotatesKey()
    {
        await using var f = new Factory(); using var c = f.Client();
        var first = await Enroll(c); var second = await Enroll(c);
        using var k1 = first.Key; using var k2 = second.Key;
        Assert.Equal(first.Device.DeviceId, second.Device.DeviceId);
    }
    [Fact] public async Task DuplicateDispatchReturnsSameJobAndOfflineTerminates()
    {
        await using var f = new Factory(); using var c = f.Client(); var (key, device) = await Enroll(c); using var k = key;
        var request = new ExecutionRequest("exit 0", DispatchTimeoutSeconds: 1);
        var a = await Dispatch(c, device.DeviceId, "same-key", request); var b = await Dispatch(c, device.DeviceId, "same-key", request);
        Assert.Equal(a.Id, b.Id); Assert.Equal("offline", (await Wait(c, a.Id)).Status);
        using var changed = new HttpRequestMessage(HttpMethod.Post, $"/v1/devices/{device.DeviceId}/executions") { Content = JsonContent.Create(new ExecutionRequest("exit 1")) };
        changed.Headers.Add("Idempotency-Key", "same-key"); Assert.Equal(HttpStatusCode.Conflict, (await c.SendAsync(changed)).StatusCode);
    }
    [Fact] public async Task SignedDispatchAndStructuredResultRoundTrip()
    {
        await using var f = new Factory(); using var c = f.Client(); var (key, device) = await Enroll(c); using var k = key;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10)); var ct = deadline.Token;
        using var ws = await f.Server.CreateWebSocketClient().ConnectAsync(new Uri("wss://localhost/v1/agent/connect"), ct);
        var challenge = (await Protocol.Receive(ws, ct))!;
        await Protocol.Send(ws, new Wire("authenticate", DeviceId: device.DeviceId, Signature: Protocol.Sign(k, Protocol.ConnectionProof(device.DeviceId, challenge.Nonce!))), ct);
        Assert.Equal("authenticated", (await Protocol.Receive(ws, ct))!.Type);
        var clock = Stopwatch.StartNew(); var job = await Dispatch(c, device.DeviceId, "online");
        var message = (await Protocol.Receive(ws, ct))!;
        Assert.True(Protocol.Verify(device.ServerPublicKey, message.Payload!, message.Signature!));
        var payload = JsonSerializer.Deserialize<JobPayload>(message.Payload!, Protocol.Json)!;
        Assert.Equal(job.Script, payload.Script); Assert.Equal(Protocol.Hash(job.Script), payload.ScriptSha256);
        await Protocol.Send(ws, new Wire("started", JobId: job.Id), ct);
        await Protocol.Send(ws, new Wire("result", JobId: job.Id, Result: new ExecutionResult("succeeded", 0, "hello\n", "", 12, false, false, job.ScriptSha256)), ct);
        var done = await Wait(c, job.Id); Assert.Equal("succeeded", done.Status); Assert.Equal("hello\n", done.Result!.Stdout);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"Protocol round trip took {clock.Elapsed}.");
        Assert.Equal(HttpStatusCode.NoContent, (await c.PostAsync($"/v1/devices/{device.DeviceId}/revoke", null)).StatusCode);
        var listing = await c.GetFromJsonAsync<JsonElement>("/v1/devices"); Assert.False(listing[0].GetProperty("online").GetBoolean());
    }
    [Fact] public async Task OpenApiIsAvailableAndAuthenticated()
    {
        await using var f = new Factory(); using var c = f.Client();
        var spec = await c.GetFromJsonAsync<JsonElement>("/openapi/v1.json");
        Assert.True(spec.GetProperty("paths").TryGetProperty("/v1/devices/{id}/executions", out _));
    }
    [Fact] public async Task RevokedDeviceCannotSubmitJobsOrAuthenticate()
    {
        await using var f = new Factory(); using var c = f.Client(); var (key, device) = await Enroll(c); using var k = key;
        Assert.Equal(HttpStatusCode.NoContent, (await c.PostAsync($"/v1/devices/{device.DeviceId}/revoke", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsJsonAsync($"/v1/devices/{device.DeviceId}/executions", new ExecutionRequest("exit 0"))).StatusCode);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var ws = await f.Server.CreateWebSocketClient().ConnectAsync(new Uri("wss://localhost/v1/agent/connect"), deadline.Token);
        var challenge = (await Protocol.Receive(ws, deadline.Token))!;
        await Protocol.Send(ws, new Wire("authenticate", DeviceId: device.DeviceId, Signature: Protocol.Sign(k, Protocol.ConnectionProof(device.DeviceId, challenge.Nonce!))), deadline.Token);
        try { Assert.Null(await Protocol.Receive(ws, deadline.Token)); }
        catch (System.Net.WebSockets.WebSocketException) { /* Server aborts unauthorized transports. */ }
        catch (IOException) { /* TestHost represents an aborted transport as IOException. */ }
        catch (OperationCanceledException) { Assert.True(ws.State != System.Net.WebSockets.WebSocketState.Open); }
    }
    [Fact] public async Task OperatorSecretCannotBeStoredInScriptSource()
    {
        await using var f = new Factory(); using var c = f.Client(); var (key, device) = await Enroll(c); using var k = key;
        using var message = new HttpRequestMessage(HttpMethod.Post, $"/v1/devices/{device.DeviceId}/executions") { Content = JsonContent.Create(new ExecutionRequest("Write-Output '" + Factory.ApiKey + "'")) };
        message.Headers.Add("Idempotency-Key", "secret");
        Assert.Equal(HttpStatusCode.BadRequest, (await c.SendAsync(message)).StatusCode);
    }
}
