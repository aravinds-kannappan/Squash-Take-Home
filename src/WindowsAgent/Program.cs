using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Squash.Agent;
using Squash.Contracts;

var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SquashRmm");
Directory.CreateDirectory(directory);
using var identity = new Identity();
if (args.Contains("--prepare"))
{
    Console.WriteLine(JsonSerializer.Serialize(new GrantRequest(identity.MachineId, identity.PublicKey), Protocol.Json));
    return;
}
var configPath = Path.Combine(directory, "agent.json");
var bootstrapPath = Path.Combine(directory, "bootstrap.json");
if (args.Contains("--enroll"))
{
    var bootstrap = JsonSerializer.Deserialize<Bootstrap>(File.ReadAllText(bootstrapPath), Protocol.Json) ?? throw new InvalidOperationException("Missing bootstrap configuration.");
    if (!Uri.TryCreate(bootstrap.ServerUrl, UriKind.Absolute, out var url) || url.Scheme != "https") throw new InvalidOperationException("HTTPS server URL required.");
    using var http = new HttpClient { BaseAddress = url, Timeout = TimeSpan.FromSeconds(20) };
    var proof = identity.Sign(Protocol.EnrollmentProof(bootstrap.Token, identity.MachineId, identity.PublicKey));
    using var response = await http.PostAsJsonAsync("/v1/agents/enroll", new EnrollRequest(bootstrap.Token, identity.MachineId, identity.PublicKey, proof, Environment.MachineName));
    if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Enrollment rejected. Check grant expiration and target public key.");
    var enrolled = await response.Content.ReadFromJsonAsync<EnrollResponse>() ?? throw new InvalidOperationException("Invalid enrollment response.");
    DurableFile.Write(configPath, new AgentConfig(bootstrap.ServerUrl, enrolled.DeviceId, enrolled.ServerPublicKey));
    File.Delete(bootstrapPath); Console.WriteLine($"Enrolled device {enrolled.DeviceId}."); return;
}
var config = JsonSerializer.Deserialize<AgentConfig>(File.ReadAllText(configPath), Protocol.Json) ?? throw new InvalidOperationException("Agent must be enrolled first.");
if (!Uri.TryCreate(config.ServerUrl, UriKind.Absolute, out var server) || server.Scheme != "https") throw new InvalidOperationException("HTTPS required.");
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = "SquashRmm");
builder.Services.AddSingleton(identity);
builder.Services.AddSingleton(config);
builder.Services.AddHostedService(s => new AgentWorker(config, identity, directory, s.GetRequiredService<ILogger<AgentWorker>>()));
await builder.Build().RunAsync();
