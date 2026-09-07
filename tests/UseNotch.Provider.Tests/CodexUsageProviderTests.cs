using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using UseNotch.Application;
using UseNotch.Domain;
using UseNotch.Providers.OpenAI;

namespace UseNotch.Provider.Tests;

public sealed class CodexUsageProviderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "UseNotch.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Source_resolution_prefers_explicit_then_environment_then_profile_and_reads_supported_toml()
    {
        var explicitRoot = Path.Combine(_root, "explicit");
        Directory.CreateDirectory(explicitRoot);
        File.WriteAllText(Path.Combine(explicitRoot, "config.toml"), "cli_auth_credentials_store = 'file'");

        var source = new CodexSourceResolver(explicitRoot, Path.Combine(_root, "environment"), Path.Combine(_root, "profile")).Resolve();

        Assert.Equal(Path.GetFullPath(explicitRoot), source.RootPath);
        Assert.Equal(CodexCredentialStorage.File, source.Storage);
        Assert.StartsWith("codex:", source.SourceId, StringComparison.Ordinal);
    }

    [Fact]
    public void Source_resolution_uses_environment_before_the_profile_default()
    {
        var environmentRoot = Path.Combine(_root, "environment");
        var profileRoot = Path.Combine(_root, "profile", ".codex");
        Directory.CreateDirectory(environmentRoot);
        Directory.CreateDirectory(profileRoot);
        File.WriteAllText(Path.Combine(environmentRoot, "config.toml"), "cli_auth_credentials_store = 'file'");
        File.WriteAllText(Path.Combine(profileRoot, "config.toml"), "cli_auth_credentials_store = 'keyring'");

        var environmentSource = new CodexSourceResolver(null, environmentRoot, Path.Combine(_root, "profile")).Resolve();
        var profileSource = new CodexSourceResolver(null, null, Path.Combine(_root, "profile")).Resolve();

        Assert.Equal(Path.GetFullPath(environmentRoot), environmentSource.RootPath);
        Assert.Equal(Path.GetFullPath(profileRoot), profileSource.RootPath);
        Assert.Equal(CodexCredentialStorage.Keyring, profileSource.Storage);
    }

    [Theory]
    [InlineData("keyring", CodexCredentialStorage.Keyring)]
    [InlineData("auto", CodexCredentialStorage.Auto)]
    [InlineData("ephemeral", CodexCredentialStorage.Ephemeral)]
    [InlineData("unknown", CodexCredentialStorage.Unsupported)]
    public void Source_resolution_recognizes_only_documented_storage_modes(string mode, CodexCredentialStorage expected)
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "config.toml"), $"cli_auth_credentials_store = '{mode}'");

        Assert.Equal(expected, new CodexSourceResolver(_root).Resolve().Storage);
    }

    [Fact]
    public async Task Credential_reader_keeps_account_identifier_and_token_out_of_snapshot_identifiers()
    {
        await WriteAuthAsync("token.one.signature", "account-private");
        var reader = new CodexCredentialReader();
        var credential = await reader.ReadAsync(new CodexSource(_root, "test", CodexCredentialStorage.File), CancellationToken.None);

        Assert.Equal("token.one.signature", credential.AccessToken);
        Assert.Equal("account-private", credential.AccountId);
        Assert.NotEqual(credential.AccessToken, credential.Fingerprint);
    }

    [Fact]
    public async Task Credential_reader_records_jwt_expiry_only_as_a_local_hint()
    {
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"exp\":1900000000}")).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        await WriteAuthAsync("header." + payload + ".signature", "account-private");

        var credential = await new CodexCredentialReader().ReadAsync(new CodexSource(_root, "test", CodexCredentialStorage.File), CancellationToken.None);

        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1900000000), credential.ExpiryHint);
    }

    [Theory]
    [InlineData("{\"OPENAI_API_KEY\":\"synthetic\"}", ErrorCategory.Schema)]
    [InlineData("{\"tokens\":{\"access_token\":\"\"}}", ErrorCategory.Schema)]
    public async Task Credential_reader_rejects_api_key_and_unknown_auth_shapes(string auth, ErrorCategory expected)
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "auth.json"), auth, CancellationToken.None);

        var exception = await Assert.ThrowsAsync<ProviderReadException>(() => new CodexCredentialReader().ReadAsync(new CodexSource(_root, "test", CodexCredentialStorage.File), CancellationToken.None));

        Assert.Equal(expected, exception.Category);
    }

    [Fact]
    public async Task Keyring_mode_uses_the_documented_per_root_target_without_vault_enumeration()
    {
        var keyring = new FakeKeyring("{\"tokens\":{\"access_token\":\"keyring.synthetic.token\",\"account_id\":\"account-private\"}}");
        var source = new CodexSource(_root, "test", CodexCredentialStorage.Keyring);

        var credential = await new CodexCredentialReader(keyring).ReadAsync(source, CancellationToken.None);

        var location = Assert.Single(keyring.Locations);
        Assert.Equal("Codex Auth", location.Service);
        Assert.StartsWith("cli|", location.Account, StringComparison.Ordinal);
        Assert.Equal(location.Account + ".Codex Auth", location.TargetName);
        Assert.Equal("keyring.synthetic.token", credential.AccessToken);
    }

    [Fact]
    public async Task Auto_mode_falls_back_to_auth_file_only_after_the_exact_keyring_entry_is_missing()
    {
        await WriteAuthAsync("file.synthetic.token", "account-private");
        var keyring = new FakeKeyring(null);

        var credential = await new CodexCredentialReader(keyring).ReadAsync(new CodexSource(_root, "test", CodexCredentialStorage.Auto), CancellationToken.None);

        Assert.Equal("file.synthetic.token", credential.AccessToken);
        Assert.Single(keyring.Locations);
    }

    [Fact]
    public void Usage_parser_preserves_primary_and_secondary_variable_windows_and_ignores_null_windows()
    {
        using var document = JsonDocument.Parse("""
            {"rate_limit":{"primary_window":{"limit_window_seconds":18000,"used_percent":40,"reset_after_seconds":3600},"secondary_window":{"limit_window_seconds":2592000,"used_percent":20,"reset_at":1900000000},"additional_rate_limits":{"used_percent":99}}}
            """);

        var windows = CodexUsageParser.Parse(document.RootElement, DateTimeOffset.FromUnixTimeSeconds(1800000000));

        Assert.Collection(windows,
            primary => { Assert.Equal("primary", primary.Id); Assert.Equal("5h limit", primary.Scope); Assert.Equal(.4m, primary.Limit.UsedFraction); },
            secondary => { Assert.Equal("secondary", secondary.Id); Assert.Equal("Monthly limit", secondary.Scope); Assert.Equal(.2m, secondary.Limit.UsedFraction); });
    }

    [Theory]
    [InlineData(401, ErrorCategory.Authentication, false)]
    [InlineData(403, ErrorCategory.Forbidden, false)]
    [InlineData(429, ErrorCategory.RateLimited, true)]
    [InlineData(500, ErrorCategory.Network, true)]
    [InlineData(302, ErrorCategory.Network, false)]
    public async Task Usage_request_maps_http_failures_without_following_responses(int status, ErrorCategory category, bool transient)
    {
        await WriteAuthAsync("token.one.signature", "account-private");
        var handler = new TestHandler(_ => new HttpResponseMessage((HttpStatusCode)status) { Headers = { RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(2)) } });
        var provider = CreateProvider(handler);

        var exception = await Assert.ThrowsAsync<ProviderReadException>(() => provider.ReadAsync(Connection(), CancellationToken.None));

        Assert.Equal(category, exception.Category);
        Assert.Equal(transient, exception.IsTransient);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Usage_request_sends_only_to_allowlisted_endpoint_and_returns_sanitized_snapshot()
    {
        await WriteAuthAsync("token.one.signature", "account-private");
        var handler = new TestHandler(request =>
        {
            Assert.Equal(CodexUsageProvider.UsageEndpoint, request.RequestUri);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("token.one.signature", request.Headers.Authorization?.Parameter);
            Assert.Equal("account-private", request.Headers.GetValues("ChatGPT-Account-Id").Single());
            return JsonResponse("{\"rate_limit\":{\"primary_window\":{\"limit_window_seconds\":18000,\"used_percent\":40,\"reset_after_seconds\":3600}}}");
        });

        var snapshot = await CreateProvider(handler).ReadAsync(Connection(), CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.Equal("primary", snapshot.HeadlineWindowId);
        Assert.DoesNotContain("private", snapshot.Account.Partition, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Codex auth.json", snapshot.Source.Kind);
    }

    [Theory]
    [InlineData("<html>not usage</html>")]
    [InlineData("{\"rate_limit\":{\"primary_window\":{\"used_percent\":null}}}")]
    public async Task Usage_request_turns_invalid_or_html_bodies_into_safe_schema_failures(string body)
    {
        await WriteAuthAsync("token.one.signature", "account-private");
        var provider = CreateProvider(new TestHandler(_ => JsonResponse(body)));

        var exception = await Assert.ThrowsAsync<ProviderReadException>(() => provider.ReadAsync(Connection(), CancellationToken.None));

        Assert.Equal(ErrorCategory.Schema, exception.Category);
    }

    [Fact]
    public async Task Unauthorized_request_retries_once_only_when_auth_file_token_rotates()
    {
        await WriteAuthAsync("token.one.signature", "account-private");
        var handler = new TestHandler(request =>
        {
            if (request.Headers.Authorization?.Parameter == "token.one.signature")
            {
                File.WriteAllText(Path.Combine(_root, "auth.json"), "{\"tokens\":{\"access_token\":\"token.two.signature\",\"account_id\":\"account-private\"}}");
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }
            return JsonResponse("{\"rate_limit\":{\"primary_window\":{\"limit_window_seconds\":18000,\"used_percent\":40}}}");
        });

        var snapshot = await CreateProvider(handler).ReadAsync(Connection(), CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.Equal(2, handler.Requests.Count);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    private CodexUsageProvider CreateProvider(HttpMessageHandler handler) => new(new CodexSourceResolver(_root), handler: handler);
    private static ProviderConnection Connection() => new(ProviderId.OpenAi, "test", true, 1);

    private async Task WriteAuthAsync(string token, string account)
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "auth.json"), $"{{\"tokens\":{{\"access_token\":\"{token}\",\"account_id\":\"{account}\"}}}}", CancellationToken.None);
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class TestHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(response(request));
        }
    }

    private sealed class FakeKeyring(string? value) : ICodexKeyring
    {
        public List<CodexKeyringLocation> Locations { get; } = [];

        public Task<byte[]?> ReadAsync(CodexKeyringLocation location, CancellationToken cancellationToken)
        {
            Locations.Add(location);
            return Task.FromResult(value is null ? null : Encoding.UTF8.GetBytes(value));
        }
    }
}
