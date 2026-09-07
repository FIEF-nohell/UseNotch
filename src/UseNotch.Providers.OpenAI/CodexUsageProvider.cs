using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tomlyn;
using Tomlyn.Model;
using Tomlyn.Serialization;
using UseNotch.Application;
using UseNotch.Domain;

namespace UseNotch.Providers.OpenAI;

public enum CodexCredentialStorage { File, Keyring, Auto, Ephemeral, Unsupported }

public sealed record CodexSource(string RootPath, string SourceId, CodexCredentialStorage Storage);

public sealed class CodexSourceResolver(string? explicitRoot = null, string? inheritedCodexHome = null, string? userProfile = null)
{
    public CodexSource Resolve()
    {
        var root = FirstUsablePath(explicitRoot)
            ?? FirstUsablePath(inheritedCodexHome)
            ?? Path.Combine(userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        root = Path.GetFullPath(root);
        return new CodexSource(root, "codex:" + StableId(root), ReadStorage(root));
    }

    private static string? FirstUsablePath(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static CodexCredentialStorage ReadStorage(string root)
    {
        var configPath = Path.Combine(root, "config.toml");
        if (!File.Exists(configPath))
        {
            return CodexCredentialStorage.File;
        }

        try
        {
            var info = new FileInfo(configPath);
            if (info.Length is <= 0 or > 128 * 1024)
            {
                return CodexCredentialStorage.Unsupported;
            }
            var model = TomlSerializer.Deserialize<CodexConfig>(File.ReadAllText(configPath), (TomlSerializerOptions?)null);
            var value = model?.CredentialStore;
            return value?.Trim().ToLowerInvariant() switch
            {
                null or "file" => CodexCredentialStorage.File,
                "keyring" => CodexCredentialStorage.Keyring,
                "auto" => CodexCredentialStorage.Auto,
                "ephemeral" => CodexCredentialStorage.Ephemeral,
                _ => CodexCredentialStorage.Unsupported,
            };
        }
        catch (IOException) { return CodexCredentialStorage.Unsupported; }
        catch (TomlException) { return CodexCredentialStorage.Unsupported; }
    }

    internal static string StableId(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16].ToLowerInvariant();

    private sealed class CodexConfig
    {
        [TomlPropertyName("cli_auth_credentials_store")]
        public string? CredentialStore { get; init; }
    }
}

public sealed record CodexCredential(string AccessToken, string AccountId, string Fingerprint, DateTimeOffset? ExpiryHint);

public sealed record CodexKeyringLocation(string Service, string Account, string TargetName);

public interface ICodexKeyring
{
    Task<byte[]?> ReadAsync(CodexKeyringLocation location, CancellationToken cancellationToken);
}

public sealed class WindowsCodexKeyring : ICodexKeyring
{
    private const uint GenericCredentialType = 1;
    private const int NotFound = 1168;
    private const int MaximumCredentialBytes = 256 * 1024;

    public Task<byte[]?> ReadAsync(CodexKeyringLocation location, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            throw new ProviderReadException("Windows credential storage is unavailable", false, category: ErrorCategory.OperatingSystem);
        }
        if (!CredRead(location.TargetName, GenericCredentialType, 0, out var pointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == NotFound)
            {
                return Task.FromResult<byte[]?>(null);
            }
            throw new ProviderReadException("Credential read denied", false, category: ErrorCategory.OperatingSystem, code: error);
        }
        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(pointer);
            if (credential.CredentialBlobSize is 0 or > MaximumCredentialBytes)
            {
                throw new ProviderReadException("Codex credential format is unsupported", false, schemaFailure: true);
            }
            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            return Task.FromResult<byte[]?>(bytes);
        }
        finally
        {
            CredFree(pointer);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [DllImport("Advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string targetName, uint type, int flags, out IntPtr credential);

    [DllImport("Advapi32.dll")]
    private static extern void CredFree(IntPtr credential);
}

public sealed class CodexCredentialReader
{
    private const long MaximumAuthBytes = 256 * 1024;
    private readonly ICodexKeyring _keyring;

    public CodexCredentialReader(ICodexKeyring? keyring = null) => _keyring = keyring ?? new WindowsCodexKeyring();

    public async Task<CodexCredential> ReadAsync(CodexSource source, CancellationToken cancellationToken)
    {
        if (source.Storage == CodexCredentialStorage.Keyring)
        {
            return Parse(await _keyring.ReadAsync(KeyringLocation(source.RootPath), cancellationToken), "Codex sign-in is required");
        }
        if (source.Storage == CodexCredentialStorage.Auto)
        {
            // Codex's auto mode loads the exact keyring entry first, then falls back to auth.json
            // when the entry is absent or the current user's credential manager cannot serve it.
            try
            {
                var bytes = await _keyring.ReadAsync(KeyringLocation(source.RootPath), cancellationToken);
                if (bytes is not null)
                {
                    return Parse(bytes, "Codex credential format is unsupported");
                }
            }
            catch (ProviderReadException)
            {
            }
        }
        if (source.Storage == CodexCredentialStorage.Ephemeral)
        {
            throw Unsupported("Codex credentials are available only to the owning process");
        }
        if (source.Storage == CodexCredentialStorage.Unsupported)
        {
            throw Unsupported("Codex credential storage configuration is unsupported");
        }

        return await ReadFileAsync(source.RootPath, cancellationToken);
    }

    public static CodexKeyringLocation KeyringLocation(string rootPath)
    {
        var canonical = Path.GetFullPath(rootPath);
        var account = "cli|" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..16].ToLowerInvariant();
        return new CodexKeyringLocation("Codex Auth", account, account + ".Codex Auth");
    }

    private static async Task<CodexCredential> ReadFileAsync(string rootPath, CancellationToken cancellationToken)
    {
        var path = Path.Combine(rootPath, "auth.json");
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                throw Auth("Codex sign-in is required");
            }
            if (info.Length is <= 0 or > MaximumAuthBytes)
            {
                throw Unsupported("Codex credential format is unsupported");
            }
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, true);
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory, cancellationToken);
            return Parse(memory.ToArray(), "Codex credential format is unsupported");
        }
        catch (FileNotFoundException) { throw Auth("Codex sign-in is required"); }
        catch (DirectoryNotFoundException) { throw Auth("Codex sign-in is required"); }
        catch (UnauthorizedAccessException) { throw new ProviderReadException("Credential read denied", false, category: ErrorCategory.OperatingSystem); }
        catch (JsonException) { throw Unsupported("Codex credential format is unsupported"); }
    }

    private static CodexCredential Parse(byte[]? bytes, string missingMessage)
    {
        if (bytes is null)
        {
            throw Auth(missingMessage);
        }
        try
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (root.TryGetProperty("OPENAI_API_KEY", out _) || root.TryGetProperty("api_key", out _))
            {
                throw Unsupported("API-key authentication does not expose ChatGPT Codex quota");
            }
            if (!root.TryGetProperty("tokens", out var tokens)
                || !tokens.TryGetProperty("access_token", out var accessToken)
                || !tokens.TryGetProperty("account_id", out var accountId)
                || string.IsNullOrWhiteSpace(accessToken.GetString())
                || string.IsNullOrWhiteSpace(accountId.GetString()))
            {
                throw Unsupported("Codex credential format is unsupported");
            }
            var token = accessToken.GetString()!;
            return new CodexCredential(token, accountId.GetString()!, Fingerprint(token), JwtExpiryHint(token));
        }
        catch (JsonException)
        {
            throw Unsupported("Codex credential format is unsupported");
        }
    }

    internal static string Fingerprint(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)))[..16].ToLowerInvariant();

    internal static DateTimeOffset? JwtExpiryHint(string token)
    {
        var pieces = token.Split('.');
        if (pieces.Length < 2)
        {
            return null;
        }

        try
        {
            var payload = pieces[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
            return document.RootElement.TryGetProperty("exp", out var expiry) && expiry.TryGetInt64(out var seconds)
                ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;
        }
        catch (FormatException) { return null; }
        catch (JsonException) { return null; }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private static ProviderReadException Auth(string message) => new(message, false, category: ErrorCategory.Authentication, code: 401);
    private static ProviderReadException Unsupported(string message) => new(message, false, schemaFailure: true);
}

public sealed class CodexUsageProvider : IUsageProvider
{
    public static readonly Uri UsageEndpoint = new("https://chatgpt.com/backend-api/wham/usage");
    private const int MaximumResponseBytes = 256 * 1024;
    private readonly CodexSourceResolver _sources;
    private readonly CodexCredentialReader _credentials;
    private readonly HttpClient _client;
    private readonly TimeProvider _clock;

    public CodexUsageProvider(CodexSourceResolver? sources = null, CodexCredentialReader? credentials = null, HttpMessageHandler? handler = null, TimeProvider? clock = null)
    {
        _sources = sources ?? new CodexSourceResolver(inheritedCodexHome: Environment.GetEnvironmentVariable("CODEX_HOME"));
        _credentials = credentials ?? new CodexCredentialReader();
        _client = new HttpClient(handler ?? CreateHandler(), disposeHandler: handler is null) { Timeout = TimeSpan.FromSeconds(15) };
        _clock = clock ?? TimeProvider.System;
    }

    public ProviderId Provider => ProviderId.OpenAi;

    public async Task<UsageSnapshot?> ReadAsync(ProviderConnection connection, CancellationToken cancellationToken)
    {
        var source = _sources.Resolve();
        var credential = await _credentials.ReadAsync(source, cancellationToken);
        return await ReadWithSingleRotationRetryAsync(source, credential, cancellationToken);
    }

    private async Task<UsageSnapshot> ReadWithSingleRotationRetryAsync(CodexSource source, CodexCredential credential, CancellationToken cancellationToken)
    {
        try { return await RequestAsync(source, credential, cancellationToken); }
        catch (ProviderReadException exception) when (exception.Code == 401)
        {
            var changed = await _credentials.ReadAsync(source, cancellationToken);
            if (changed.Fingerprint == credential.Fingerprint)
            {
                throw;
            }

            return await RequestAsync(source, changed, cancellationToken);
        }
    }

    private async Task<UsageSnapshot> RequestAsync(CodexSource source, CodexCredential credential, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, UsageEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.AccessToken);
        request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", credential.AccountId);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var now = _clock.GetUtcNow();
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new ProviderReadException("Credential rejected", false, category: ErrorCategory.Authentication, code: 401);
        }

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new ProviderReadException("Credential forbidden", false, category: ErrorCategory.Forbidden, code: 403);
        }

        if (response.StatusCode == (HttpStatusCode)429)
        {
            throw new ProviderReadException("Provider rate limit reached", true, serverDeadline: RetryAfterParser.TryParseDeadline(response.Headers.RetryAfter, now), category: ErrorCategory.RateLimited, code: 429);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new ProviderReadException("Provider request failed", (int)response.StatusCode >= 500, category: ErrorCategory.Network, code: (int)response.StatusCode);
        }

        if (response.Content.Headers.ContentLength is > MaximumResponseBytes)
        {
            throw new ProviderReadException("Provider response format is unsupported", false, schemaFailure: true);
        }

        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(new BoundedReadStream(stream, MaximumResponseBytes), cancellationToken: cancellationToken);
            var windows = CodexUsageParser.Parse(document.RootElement, now);
            return new UsageSnapshot(ProviderId.OpenAi, new AccountScope(CodexSourceResolver.StableId(credential.AccountId), IdentityConfidence.LocalPartition), now, windows, windows[0].Id, new SourceDescriptor("Codex auth.json", "codex-file-v1", false), null);
        }
        catch (JsonException)
        {
            throw new ProviderReadException("Provider response format is unsupported", false, schemaFailure: true);
        }
        catch (IOException)
        {
            throw new ProviderReadException("Provider response format is unsupported", false, schemaFailure: true);
        }
    }

    private static HttpMessageHandler CreateHandler() => new HttpClientHandler { AllowAutoRedirect = false };
}

public static class CodexUsageParser
{
    public static IReadOnlyList<QuotaWindow> Parse(JsonElement root, DateTimeOffset now)
    {
        if (!TryGetRateLimit(root, out var rateLimit))
        {
            throw new ProviderReadException("Provider response format is unsupported", false, schemaFailure: true);
        }

        var result = new List<QuotaWindow>();
        Add(result, rateLimit, ["primary_window", "five_hour"], "primary", now);
        Add(result, rateLimit, ["secondary_window", "weekly"], "secondary", now);
        if (result.Count == 0)
        {
            throw new ProviderReadException("Codex reported no usable quota windows", false, schemaFailure: true);
        }

        return result;
    }

    private static bool TryGetRateLimit(JsonElement root, out JsonElement rateLimit)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            rateLimit = default;
            return false;
        }

        if (TryGetObject(root, "rate_limit", out rateLimit) || TryGetObject(root, "rate_limits", out rateLimit))
        {
            return true;
        }

        if (TryGetObject(root, "data", out var data)
            && (TryGetObject(data, "rate_limit", out rateLimit) || TryGetObject(data, "rate_limits", out rateLimit)))
        {
            return true;
        }

        rateLimit = default;
        return false;
    }

    private static bool TryGetObject(JsonElement parent, string property, out JsonElement value)
        => parent.TryGetProperty(property, out value) && value.ValueKind == JsonValueKind.Object;

    private static void Add(List<QuotaWindow> result, JsonElement parent, string[] properties, string id, DateTimeOffset now)
    {
        foreach (var property in properties)
        {
            if (TryGetObject(parent, property, out var window) && TryCreateWindow(window, id, now, out var quotaWindow))
            {
                result.Add(quotaWindow);
                return;
            }
        }
    }

    private static bool TryCreateWindow(JsonElement window, string id, DateTimeOffset now, out QuotaWindow quotaWindow)
    {
        quotaWindow = default!;
        if (!window.TryGetProperty("limit_window_seconds", out var durationElement) || durationElement.ValueKind != JsonValueKind.Number || !durationElement.TryGetDouble(out var seconds) || seconds <= 0
            || !TryGetUsedPercent(window, out var percent))
        {
            return false;
        }

        var reset = GetReset(window, now);
        var duration = TimeSpan.FromSeconds(seconds);
        quotaWindow = new QuotaWindow(id, Label(duration, id), duration, reset - duration, reset, new UsageLimit(null, null, null, percent / 100m, "percent"));
        return true;
    }

    private static bool TryGetUsedPercent(JsonElement window, out decimal? percent)
    {
        percent = null;
        if (window.TryGetProperty("used_percent", out var used))
        {
            if (used.ValueKind == JsonValueKind.Null)
            {
                return true;
            }

            if (used.ValueKind == JsonValueKind.Number && used.TryGetDecimal(out var parsed) && parsed is >= 0 and <= 100)
            {
                percent = parsed;
                return true;
            }

            return false;
        }

        if (window.TryGetProperty("percent_left", out var remaining)
            && remaining.ValueKind == JsonValueKind.Number
            && remaining.TryGetDecimal(out var remainingPercent)
            && remainingPercent is >= 0 and <= 100)
        {
            percent = 100m - remainingPercent;
            return true;
        }

        return false;
    }

    private static DateTimeOffset? GetReset(JsonElement window, DateTimeOffset now)
    {
        if (window.TryGetProperty("reset_at", out var absolute))
        {
            if (absolute.ValueKind == JsonValueKind.Number && absolute.TryGetInt64(out var resetSeconds) && TryFromUnixTimeSeconds(resetSeconds, out var reset))
            {
                return reset;
            }

            if (absolute.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(absolute.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out reset))
            {
                return reset;
            }
        }

        if (window.TryGetProperty("reset_time_ms", out var milliseconds)
            && milliseconds.ValueKind == JsonValueKind.Number
            && milliseconds.TryGetInt64(out var resetMilliseconds)
            && TryFromUnixTimeMilliseconds(resetMilliseconds, out var resetAtMilliseconds))
        {
            return resetAtMilliseconds;
        }

        return window.TryGetProperty("reset_after_seconds", out var relative)
            && relative.ValueKind == JsonValueKind.Number
            && relative.TryGetDouble(out var delay)
            && delay >= 0
            ? now.AddSeconds(delay)
            : null;
    }

    private static bool TryFromUnixTimeSeconds(long value, out DateTimeOffset timestamp)
    {
        try { timestamp = DateTimeOffset.FromUnixTimeSeconds(value); return true; }
        catch (ArgumentOutOfRangeException) { timestamp = default; return false; }
    }

    private static bool TryFromUnixTimeMilliseconds(long value, out DateTimeOffset timestamp)
    {
        try { timestamp = DateTimeOffset.FromUnixTimeMilliseconds(value); return true; }
        catch (ArgumentOutOfRangeException) { timestamp = default; return false; }
    }

    internal static string Label(TimeSpan duration, string fallback) => duration.TotalMinutes switch
    {
        < 60 => $"{Math.Round(duration.TotalMinutes):0}m limit",
        < 1440 => $"{Math.Round(duration.TotalHours):0}h limit",
        _ when Math.Round(duration.TotalDays) == 7 => "Weekly limit",
        _ when Math.Round(duration.TotalDays) == 30 => "Monthly limit",
        _ => $"{Math.Round(duration.TotalDays):0}d limit",
    };
}

internal sealed class BoundedReadStream(Stream inner, int maximumBytes) : Stream
{
    private int _read;
    public override bool CanRead => inner.CanRead; public override bool CanSeek => false; public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) => Guard(inner.Read(buffer, offset, count));
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => Guard(await inner.ReadAsync(buffer, cancellationToken));
    private int Guard(int count) { _read += count; if (_read > maximumBytes) { throw new IOException("Response exceeded its limit."); } return count; }
}
