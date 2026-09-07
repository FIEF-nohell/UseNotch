using System.Globalization;
using System.Text.RegularExpressions;

namespace UseNotch.Infrastructure;

/// <summary>
/// Removes anything that could identify an account or a person from a diagnostic line. There is no
/// option anywhere in this application that writes a raw token or a raw response body.
/// </summary>
public static partial class DiagnosticSanitizer
{
    public const int MaximumLineLength = 500;

    public static string Sanitize(string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return string.Empty;
        }

        var sanitized = TokenPattern().Replace(message, "[redacted]");
        sanitized = JwtPattern().Replace(sanitized, "[redacted]");
        sanitized = EmailPattern().Replace(sanitized, "[redacted]");
        sanitized = BearerPattern().Replace(sanitized, "Bearer [redacted]");
        sanitized = WindowsPathPattern().Replace(sanitized, "[path]");
        sanitized = UserProfilePattern().Replace(sanitized, "[path]");
        sanitized = sanitized.ReplaceLineEndings(" ");
        return sanitized.Length > MaximumLineLength ? sanitized[..MaximumLineLength] : sanitized;
    }

    [GeneratedRegex(@"\b(sk|pk|rk)[-_][A-Za-z0-9\-_]{8,}", RegexOptions.None, 200)]
    private static partial Regex TokenPattern();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9\-_]{8,}(?:\.[A-Za-z0-9\-_]+){0,2}", RegexOptions.None, 200)]
    private static partial Regex JwtPattern();

    [GeneratedRegex(@"\b[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}\b", RegexOptions.None, 200)]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"\bBearer\s+\S+", RegexOptions.IgnoreCase, 200)]
    private static partial Regex BearerPattern();

    [GeneratedRegex(@"[A-Za-z]:\\[^\s""']*", RegexOptions.None, 200)]
    private static partial Regex WindowsPathPattern();

    [GeneratedRegex(@"(?:\\\\|/)Users(?:\\|/)[^\s""'\\/]+", RegexOptions.IgnoreCase, 200)]
    private static partial Regex UserProfilePattern();
}

/// <summary>
/// A small opt-in diagnostic log. It is off unless the user turns it on, every line is sanitized, and
/// the file is capped so it cannot grow without bound.
/// </summary>
public sealed class SanitizedDiagnosticLog(string? directory = null, long maximumBytes = 256 * 1024)
{
    public const string FileName = "diagnostics.log";

    private readonly string _directory = directory ?? ApplicationPaths.Logs;
    private readonly object _gate = new();

    public bool IsEnabled { get; set; }

    public string Path => System.IO.Path.Combine(_directory, FileName);

    public void Write(string message)
    {
        if (!IsEnabled)
        {
            return;
        }

        var line = string.Create(
            CultureInfo.InvariantCulture,
            $"{DateTimeOffset.UtcNow:yyyy-MM-ddTHH:mm:ssZ} {DiagnosticSanitizer.Sanitize(message)}");
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(_directory);
                var path = Path;
                if (File.Exists(path) && new FileInfo(path).Length > maximumBytes)
                {
                    // Keep one previous file at most. Old diagnostics are not worth unbounded disk.
                    File.Move(path, path + ".1", true);
                }

                File.AppendAllLines(path, [line]);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
