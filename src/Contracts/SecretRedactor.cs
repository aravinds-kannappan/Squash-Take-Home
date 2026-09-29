using System.Text;
using System.Text.RegularExpressions;

namespace Squash.Contracts;

/// <summary>Scrubs configured literals and common credential formats before persistence.</summary>
public sealed class SecretRedactor(IEnumerable<string> literals)
{
    readonly string[] literals = literals.Where(s => !string.IsNullOrEmpty(s)).Distinct().OrderByDescending(s => s.Length).ToArray();
    static readonly Regex[] Patterns =
    [
        new(@"-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----[\s\S]*?-----END (?:RSA |EC |OPENSSH )?PRIVATE KEY-----", RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100)),
        new(@"(?i)\bBearer\s+[a-z0-9._~+/=\-]{12,}", RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100)),
        new(@"\b(?:AKIA[0-9A-Z]{16}|gh[pousr]_[A-Za-z0-9]{20,}|sk-(?:ant-)?[A-Za-z0-9_\-]{20,})\b", RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100)),
        new("(?i)\\b(?:password|passwd|secret|token|api[_-]?key|authorization)\\b[\\\"']?\\s*[:=]\\s*[\\\"']?[^\\s\\\"',;}]{4,}", RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100))
    ];
    public string Clean(string value)
    {
        foreach (var secret in literals) value = value.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
        foreach (var pattern in Patterns) value = pattern.Replace(value, "[REDACTED]");
        return value;
    }
    public bool ContainsSecret(string value) => Clean(value) != value;
    public ExecutionResult Clean(ExecutionResult result)
    {
        var stdout = Bound(Clean(result.Stdout)); var stderr = Bound(Clean(result.Stderr));
        return result with { Stdout = stdout.Text, Stderr = stderr.Text,
            StdoutTruncated = result.StdoutTruncated || stdout.Truncated,
            StderrTruncated = result.StderrTruncated || stderr.Truncated,
            Error = result.Error is null ? null : Clean(result.Error) };
    }
    static (string Text, bool Truncated) Bound(string text)
    {
        if (Encoding.UTF8.GetByteCount(text) <= Protocol.MaxOutputBytes) return (text, false);
        var bytes = Encoding.UTF8.GetBytes(text);
        var bounded = Encoding.UTF8.GetString(bytes, 0, Protocol.MaxOutputBytes);
        while (Encoding.UTF8.GetByteCount(bounded) > Protocol.MaxOutputBytes) bounded = bounded[..^1];
        return (bounded, true);
    }
}
