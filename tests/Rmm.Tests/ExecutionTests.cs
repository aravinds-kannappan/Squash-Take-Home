using System.Security.Cryptography;
using System.Text;
using Squash.Agent;
using Squash.Contracts;
using Squash.ControlPlane;
using Xunit;
using Microsoft.Extensions.Configuration;

namespace Rmm.Tests;

public sealed class ExecutionTests
{
    [Theory]
    [InlineData("password=hunter22")]
    [InlineData("Authorization: Bearer abcdefghijklmnopqrstuvwxyz")]
    [InlineData("ghp_abcdefghijklmnopqrstuvwxyz1234567890")]
    [InlineData("-----BEGIN PRIVATE KEY-----\nprivate-bytes\n-----END PRIVATE KEY-----")]
    public void CommonCredentialFormatsAreScrubbed(string value)
    {
        var redactor = new SecretRedactor([]);
        Assert.True(redactor.ContainsSecret(value));
        Assert.Equal("[REDACTED]", redactor.Clean(value));
    }
    [Fact] public void RedactionCannotExpandBeyondOutputLimit()
    {
        var result = new SecretRedactor(["x"]).Clean(new ExecutionResult("succeeded", 0, new string('x', Protocol.MaxOutputBytes), "", 1, false, false, "hash"));
        Assert.True(result.StdoutTruncated); Assert.True(Encoding.UTF8.GetByteCount(result.Stdout) <= Protocol.MaxOutputBytes);
    }
    [Fact] public void KnownSecretsAreRedactedFromResults()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Rmm:ApiKey"] = "test-secret-key" }).Build();
        var result = new ResultRedactor(config).Clean(new ExecutionResult("failed", 1, "value: test-secret-key", "test-secret-key", 0, false, false, "hash", "test-secret-key"));
        Assert.DoesNotContain("test-secret-key", result.Stdout); Assert.Equal("[REDACTED]", result.Stderr); Assert.Equal("[REDACTED]", result.Error);
    }
    [Fact] public void AlteredScriptsFailSignatureVerification()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signature = Protocol.Sign(key, "original");
        Assert.True(Protocol.Verify(Protocol.PublicKey(key), "original", signature));
        Assert.False(Protocol.Verify(Protocol.PublicKey(key), "modified", signature));
    }
    [Fact] public async Task OutputIsBoundedAndContinuesDraining()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(new string('x', Protocol.MaxOutputBytes * 3)));
        var result = await PowerShellRunner.Drain(stream, CancellationToken.None);
        Assert.True(result.Truncated); Assert.Equal(Protocol.MaxOutputBytes, Encoding.UTF8.GetByteCount(result.Text)); Assert.Equal(stream.Length, stream.Position);
    }
    [Fact] public void RestartDoesNotRedispatchAnUncertainExecution()
    {
        var directory = Path.Combine(Path.GetTempPath(), "squash-store-" + Guid.NewGuid().ToString("N")); var path = Path.Combine(directory, "test.db"); string id;
        using (var store = new Store(path)) { var j = store.Create("device", "key", new ExecutionRequest("exit 0"), "test").Job; id = j.Id; store.Dispatch(id); }
        using (var store = new Store(path))
        { store.Sweep(startup: true); Assert.Equal("interrupted", store.Job(id)!.Status); Assert.Null(store.Dispatch(id)); }
        Directory.Delete(directory, true);
    }
    [Fact] public void LateResultCannotOverwriteTerminalStatus()
    {
        var directory = Path.Combine(Path.GetTempPath(), "squash-store-" + Guid.NewGuid().ToString("N"));
        using (var store = new Store(Path.Combine(directory, "test.db")))
        {
            var job = store.Create("device", "key", new ExecutionRequest("exit 0"), "test").Job;
            Assert.True(store.Complete(job.Id, Store.Failure(job, "timed_out", "deadline")));
            Assert.False(store.Complete(job.Id, new ExecutionResult("succeeded", 0, "", "", 1, false, false, job.ScriptSha256)));
            Assert.Equal("timed_out", store.Job(job.Id)!.Status);
        }
        Directory.Delete(directory, true);
    }
    [WindowsFact] public async Task RealPowerShellReturnsOutputAndNonzeroExit()
    {
        var dir = Path.Combine(Path.GetTempPath(), "squash-run-" + Guid.NewGuid().ToString("N"));
        const string script = "Write-Output 'hello café 世界'; [Console]::Error.WriteLine('problem'); exit 7";
        var result = await new PowerShellRunner(dir).Run(new JobPayload(Guid.NewGuid().ToString("N"), "device", script, Protocol.Hash(script), 5, DateTimeOffset.UtcNow.AddMinutes(1), "nonce"), CancellationToken.None);
        Assert.Equal("failed", result.Status); Assert.Equal(7, result.ExitCode); Assert.Contains("hello café 世界", result.Stdout); Assert.Contains("problem", result.Stderr);
        Directory.Delete(dir, true);
    }
    [WindowsFact] public async Task RealPowerShellTimeoutTerminates()
    {
        var dir = Path.Combine(Path.GetTempPath(), "squash-run-" + Guid.NewGuid().ToString("N")); const string script = "Start-Sleep -Seconds 30";
        var result = await new PowerShellRunner(dir).Run(new JobPayload(Guid.NewGuid().ToString("N"), "device", script, Protocol.Hash(script), 1, DateTimeOffset.UtcNow.AddMinutes(1), "nonce"), CancellationToken.None);
        Assert.Equal("timed_out", result.Status); Assert.InRange(result.DurationMs, 900, 5000); Directory.Delete(dir, true);
    }
}
public sealed class WindowsFactAttribute : FactAttribute
{ public WindowsFactAttribute() { if (!OperatingSystem.IsWindows()) Skip = "Requires an actual Windows PowerShell endpoint."; } }
