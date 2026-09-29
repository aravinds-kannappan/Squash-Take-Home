using Squash.Contracts;

namespace Squash.ControlPlane;

public sealed class ResultRedactor(IConfiguration configuration)
{
    readonly string[] secrets = new[] { configuration["Rmm:ApiKey"] }
        .Concat(configuration.GetSection("Rmm:RedactSecrets").Get<string[]>() ?? [])
        .Where(s => !string.IsNullOrEmpty(s)).Select(s => s!).Distinct().ToArray();
    public string Clean(string text)
    {
        foreach (var secret in secrets) text = text.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
        return text;
    }
    public ExecutionResult Clean(ExecutionResult result) => result with
    { Stdout = Clean(result.Stdout), Stderr = Clean(result.Stderr), Error = result.Error is null ? null : Clean(result.Error) };
}
