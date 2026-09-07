using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UseNotch.Application;
using UseNotch.Domain;

namespace UseNotch.Providers.Anthropic;

public sealed record ClaudeSource(string RootPath, string SourceId);

public sealed class ClaudeSourceResolver(string? explicitRoot = null, string? inheritedClaudeConfigDir = null, string? userProfile = null)
{
    public ClaudeSource Resolve()
    {
        var root = string.IsNullOrWhiteSpace(explicitRoot) ? inheritedClaudeConfigDir : explicitRoot;
        root = string.IsNullOrWhiteSpace(root) ? Path.Combine(userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude") : root.Trim();
        root = Path.GetFullPath(root);
        return new ClaudeSource(root, "claude:" + ClaudeCredentialReader.Fingerprint(root));
    }
}

public sealed record ClaudeCredential(string AccessToken, string Fingerprint, DateTimeOffset? ExpiryHint, string? SubscriptionType);

public sealed class ClaudeCredentialReader
{
    private const long MaximumCredentialBytes = 256 * 1024;

    public async Task<ClaudeCredential> ReadAsync(ClaudeSource source, CancellationToken cancellationToken)
    {
        var path = Path.Combine(source.RootPath, ".credentials.json");
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) { throw Missing("Claude Code sign-in is required"); }
                if (info.Length is <= 0 or > MaximumCredentialBytes) { throw Unsupported("Claude Code credential format is unsupported"); }
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, true);
                using var memory = new MemoryStream();
                await stream.CopyToAsync(memory, cancellationToken);
                return Parse(memory.ToArray());
            }
            catch (JsonException) when (attempt == 0)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
            }
            catch (IOException) when (attempt == 0)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
            }
            catch (JsonException)
            {
                break;
            }
            catch (IOException)
            {
                break;
            }
        }
        throw Unsupported("Claude Code credential format is unsupported");
    }

    internal static string Fingerprint(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16].ToLowerInvariant();

    private static ClaudeCredential Parse(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        if (!document.RootElement.TryGetProperty("claudeAiOauth", out var oauth) || oauth.ValueKind != JsonValueKind.Object
            || !oauth.TryGetProperty("accessToken", out var token) || token.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(token.GetString()))
        {
            throw Unsupported("Quota unavailable for this authentication method");
        }
        DateTimeOffset? expiry = null;
        if (oauth.TryGetProperty("expiresAt", out var expires) && expires.ValueKind == JsonValueKind.Number && expires.TryGetInt64(out var milliseconds))
        {
            try { expiry = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds); } catch (ArgumentOutOfRangeException) { }
        }
        var subscription = oauth.TryGetProperty("subscriptionType", out var type) && type.ValueKind == JsonValueKind.String ? type.GetString() : null;
        var accessToken = token.GetString()!;
        return new ClaudeCredential(accessToken, Fingerprint(accessToken), expiry, subscription);
    }

    private static ProviderReadException Missing(string message) => new(message, false, category: ErrorCategory.Authentication, code: 401, authenticationHint: AuthenticationState.Missing);
    private static ProviderReadException Unsupported(string message) => new(message, false, schemaFailure: true, authenticationHint: AuthenticationState.Unsupported);
}

public sealed class ClaudeUsageProvider(ClaudeSourceResolver? sources = null, ClaudeCredentialReader? credentials = null, HttpMessageHandler? handler = null, TimeProvider? clock = null) : IUsageProvider
{
    public static readonly Uri UsageEndpoint = new("https://api.anthropic.com/api/oauth/usage");
    private const int MaximumResponseBytes = 256 * 1024;
    private readonly ClaudeSourceResolver _sources = sources ?? new ClaudeSourceResolver(inheritedClaudeConfigDir: Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR"));
    private readonly ClaudeCredentialReader _credentials = credentials ?? new ClaudeCredentialReader();
    private readonly HttpClient _client = new(handler ?? CreateHandler(), disposeHandler: handler is null) { Timeout = TimeSpan.FromSeconds(15) };
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public ProviderId Provider => ProviderId.Anthropic;

    public async Task<UsageSnapshot?> ReadAsync(ProviderConnection connection, CancellationToken cancellationToken)
    {
        var source = _sources.Resolve();
        var credential = await _credentials.ReadAsync(source, cancellationToken);
        try { return await RequestAsync(source, credential, cancellationToken); }
        catch (ProviderReadException exception) when (exception.Code == 401)
        {
            var changed = await _credentials.ReadAsync(source, cancellationToken);
            if (changed.Fingerprint == credential.Fingerprint) { throw; }
            return await RequestAsync(source, changed, cancellationToken);
        }
    }

    private async Task<UsageSnapshot> RequestAsync(ClaudeSource source, ClaudeCredential credential, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, UsageEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.AccessToken);
        request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var now = _clock.GetUtcNow();
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            // The server, not a local JWT decision, confirms the credential no longer works. A past local
            // expiry hint only refines the message to "expired" versus an outright "rejected" credential.
            var expired = credential.ExpiryHint is { } expiry && expiry <= now;
            throw new ProviderReadException(expired ? "Claude Code session expired" : "Credential rejected", false, category: ErrorCategory.Authentication, code: 401, authenticationHint: expired ? AuthenticationState.Expired : AuthenticationState.Rejected);
        }
        if (response.StatusCode == HttpStatusCode.Forbidden) { throw new ProviderReadException("Credential forbidden", false, category: ErrorCategory.Forbidden, code: 403, authenticationHint: AuthenticationState.AccessDenied); }
        if (response.StatusCode == (HttpStatusCode)429) { throw new ProviderReadException("Provider rate limit reached", true, serverDeadline: RetryAfterParser.TryParseDeadline(response.Headers.RetryAfter, now), category: ErrorCategory.RateLimited, code: 429); }
        if (!response.IsSuccessStatusCode) { throw new ProviderReadException("Provider request failed", (int)response.StatusCode >= 500, category: ErrorCategory.Network, code: (int)response.StatusCode); }
        if (response.Content.Headers.ContentLength is > MaximumResponseBytes) { throw Unsupported(); }
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(new BoundedClaudeStream(stream, MaximumResponseBytes), cancellationToken: cancellationToken);
            var windows = ClaudeUsageParser.Parse(document.RootElement, now);
            return new UsageSnapshot(ProviderId.Anthropic, new AccountScope(credential.Fingerprint, IdentityConfidence.LocalPartition), now, windows, windows[0].Id, new SourceDescriptor("Claude Code credentials", "claude-file-v1", false), null);
        }
        catch (JsonException) { throw Unsupported(); }
        catch (IOException) { throw Unsupported(); }
    }

    private static ProviderReadException Unsupported() => new("Provider response format is unsupported", false, schemaFailure: true, authenticationHint: AuthenticationState.Unsupported);
    private static HttpMessageHandler CreateHandler() => new HttpClientHandler { AllowAutoRedirect = false };
}

public static class ClaudeUsageParser
{
    public static IReadOnlyList<QuotaWindow> Parse(JsonElement root, DateTimeOffset now)
    {
        if (root.ValueKind != JsonValueKind.Object) { throw Unsupported(); }
        var windows = new List<QuotaWindow>();
        if (root.TryGetProperty("limits", out var limits) && limits.ValueKind == JsonValueKind.Array)
        {
            foreach (var limit in limits.EnumerateArray()) { Add(windows, limit, null, now); }
        }
        Add(windows, root, "five_hour", now);
        Add(windows, root, "seven_day", now);
        if (windows.Count == 0) { throw Unsupported(); }
        return windows.OrderBy(window => window.Id == "session" ? 0 : window.Id == "weekly_all" ? 1 : 2).ThenBy(window => window.Id).ToArray();
    }

    private static void Add(List<QuotaWindow> result, JsonElement parent, string? property, DateTimeOffset now)
    {
        var window = property is null ? parent : parent.TryGetProperty(property, out var nested) ? nested : default;
        if (window.ValueKind != JsonValueKind.Object) { return; }
        var rawKind = property ?? (window.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.String ? kind.GetString() : null);
        var id = rawKind switch { "five_hour" => "session", "seven_day" => "weekly_all", "session" => "session", "weekly_all" => "weekly_all", _ => rawKind };
        var percentProperty = property is null ? "percent" : "utilization";
        if (id is null || !window.TryGetProperty(percentProperty, out var percent) || percent.ValueKind != JsonValueKind.Number || !percent.TryGetDecimal(out var usage) || usage is < 0 or > 100 || result.Any(existing => existing.Id == id)) { return; }
        var reset = ReadTimestamp(window, "resets_at") ?? ReadTimestamp(window, "reset_at");
        result.Add(new QuotaWindow(id, Label(id), null, null, reset, new UsageLimit(null, null, null, usage / 100m, "percent")));
    }

    private static DateTimeOffset? ReadTimestamp(JsonElement value, string property) => value.TryGetProperty(property, out var timestamp) && timestamp.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(timestamp.GetString(), out var parsed) ? parsed.ToUniversalTime() : null;
    private static string Label(string id) => id switch { "session" => "Current session", "weekly_all" => "All models", "weekly_opus" => "Opus", "weekly_sonnet" => "Sonnet", _ => id.Replace("weekly_", "", StringComparison.Ordinal).Replace('_', ' ') };
    private static ProviderReadException Unsupported() => new("Provider response format is unsupported", false, schemaFailure: true, authenticationHint: AuthenticationState.Unsupported);
}

internal sealed class BoundedClaudeStream(Stream inner, int maximumBytes) : Stream
{
    private int _read;
    public override bool CanRead => inner.CanRead; public override bool CanSeek => false; public override bool CanWrite => false; public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) => Guard(inner.Read(buffer, offset, count)); public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => Guard(await inner.ReadAsync(buffer, cancellationToken));
    private int Guard(int count) { _read += count; if (_read > maximumBytes) { throw new IOException("Response exceeded its limit."); } return count; }
}
