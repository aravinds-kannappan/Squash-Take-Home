using Squash.Contracts;

namespace Squash.ControlPlane;

public sealed class ResultRedactor(IConfiguration configuration)
{
    readonly SecretRedactor redactor = new(new[] { configuration["Rmm:ApiKey"] }
        .Concat(configuration.GetSection("Rmm:RedactSecrets").Get<string[]>() ?? [])
        .Where(s => !string.IsNullOrEmpty(s)).Select(s => s!));
    public string Clean(string text) => redactor.Clean(text);
    public bool ContainsSecret(string text) => redactor.ContainsSecret(text);
    public ExecutionResult Clean(ExecutionResult result) => redactor.Clean(result);
}
