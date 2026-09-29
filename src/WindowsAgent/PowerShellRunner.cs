using System.Diagnostics;
using System.Text;
using Squash.Contracts;

namespace Squash.Agent;

public sealed class PowerShellRunner(string workDirectory)
{
    readonly SecretRedactor redactor = new(Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
        .Where(e => new[] { "SECRET", "TOKEN", "PASSWORD", "API_KEY", "CREDENTIAL" }.Any(k => e.Key.ToString()!.Contains(k, StringComparison.OrdinalIgnoreCase)))
        .Select(e => e.Value?.ToString() ?? "").Where(s => s.Length >= 8));
    public async Task<ExecutionResult> Run(JobPayload job, CancellationToken cancellation)
    {
        Directory.CreateDirectory(workDirectory);
        var path = Path.Combine(workDirectory, job.Id + ".ps1");
        var clock = Stopwatch.StartNew();
        try
        {
            // BOM makes Windows PowerShell 5.1 decode non-ASCII source correctly.
            await File.WriteAllTextAsync(path, job.Script, new UTF8Encoding(true), cancellation);
            var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
            using var process = new Process { StartInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = workDirectory, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            }};
            // Script execution is gated until the process belongs to the Job Object.
            // If the agent dies before assignment, only this fixed bootstrap runs;
            // it times out without invoking user code.
            var gateName = @"Local\SquashRmm-" + Guid.NewGuid().ToString("N");
            using var executionGate = new EventWaitHandle(false, EventResetMode.ManualReset, gateName);
            var bootstrap = "$rmmGate=[Threading.EventWaitHandle]::OpenExisting('" + gateName + "'); if(-not $rmmGate.WaitOne(10000)){exit 124}; $rmmGate.Dispose(); " +
                "[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false); & '" + path.Replace("'", "''") + "'; $rmmSuccess = $?; if ($null -ne $LASTEXITCODE) { exit $LASTEXITCODE }; if (-not $rmmSuccess) { exit 1 }";
            foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-OutputFormat", "Text", "-ExecutionPolicy", "Bypass", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(bootstrap)) }) process.StartInfo.ArgumentList.Add(arg);
            // Never inherit bootstrap tokens or API keys into the script environment.
            foreach (var key in process.StartInfo.Environment.Keys.Where(k => k.StartsWith("Rmm", StringComparison.OrdinalIgnoreCase) || new[] { "SECRET", "TOKEN", "PASSWORD", "API_KEY", "CREDENTIAL" }.Any(s => k.Contains(s, StringComparison.OrdinalIgnoreCase))).ToArray())
                process.StartInfo.Environment.Remove(key);
            process.Start();
            using var containment = WindowsJob.Attach(process);
            executionGate.Set();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            deadline.CancelAfter(TimeSpan.FromSeconds(job.TimeoutSeconds));
            using var drainDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(job.TimeoutSeconds + 3));
            var stdout = Drain(process.StandardOutput.BaseStream, drainDeadline.Token);
            var stderr = Drain(process.StandardError.BaseStream, drainDeadline.Token);
            var status = "succeeded";
            try { await process.WaitForExitAsync(deadline.Token); if (process.ExitCode != 0) status = "failed"; }
            catch (OperationCanceledException)
            {
                status = cancellation.IsCancellationRequested ? "interrupted" : "timed_out";
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                await process.WaitForExitAsync(CancellationToken.None);
            }
            // Closing the Job Object kills children even if the parent exited successfully.
            containment.Dispose();
            var output = await stdout; var error = await stderr;
            return redactor.Clean(new ExecutionResult(status, process.ExitCode, output.Text, error.Text, clock.ElapsedMilliseconds, output.Truncated, error.Truncated, job.ScriptSha256));
        }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or InvalidOperationException or OperationCanceledException)
        { return new("failed", null, "", "", clock.ElapsedMilliseconds, false, false, job.ScriptSha256, "PowerShell could not complete. Check endpoint service permissions."); }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
    public static async Task<(string Text, bool Truncated)> Drain(Stream stream, CancellationToken ct)
    {
        using var output = new MemoryStream(); var buffer = new byte[8192]; bool truncated = false;
        try
        {
            int count;
            while ((count = await stream.ReadAsync(buffer, ct)) != 0)
            {
                var keep = Math.Min(count, Protocol.MaxOutputBytes - (int)output.Length);
                output.Write(buffer, 0, keep); if (keep < count) truncated = true;
            }
        }
        catch (OperationCanceledException) { truncated = true; }
        // A trailing partial UTF-8 character must not expand beyond the byte budget.
        var text = Encoding.UTF8.GetString(output.ToArray());
        while (Encoding.UTF8.GetByteCount(text) > Protocol.MaxOutputBytes) text = text[..^1];
        return (text, truncated);
    }
}
