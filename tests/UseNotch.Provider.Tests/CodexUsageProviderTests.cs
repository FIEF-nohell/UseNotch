using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using UseNotch.Application;
using UseNotch.Domain;
using UseNotch.Providers.Anthropic;
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

    [Theory]
    // A signed-in ChatGPT installation observed on 2026-09-07 writes a null "OPENAI_API_KEY" next to its
    // ChatGPT tokens, and an "auth_mode" naming the mode the owning tool actually uses.
    [InlineData("{\"OPENAI_API_KEY\":null,\"auth_mode\":\"chatgpt\",\"last_refresh\":\"2026-09-07T08:14:00Z\",\"tokens\":{\"access_token\":\"synthetic.token\",\"account_id\":\"synthetic-account\",\"id_token\":\"synthetic\",\"refresh_token\":\"synthetic\"}}", true)]
    [InlineData("{\"OPENAI_API_KEY\":\"synthetic\",\"auth_mode\":\"apikey\",\"tokens\":{\"access_token\":\"synthetic.token\",\"account_id\":\"synthetic-account\"}}", false)]
    [InlineData("{\"OPENAI_API_KEY\":\"synthetic\",\"tokens\":null}", false)]
    public async Task Credential_reader_treats_only_a_real_api_key_mode_as_unsupported(string auth, bool expectChatGptCredential)
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "auth.json"), auth, CancellationToken.None);
        var source = new CodexSource(_root, "test", CodexCredentialStorage.File);

        if (expectChatGptCredential)
        {
            var credential = await new CodexCredentialReader().ReadAsync(source, CancellationToken.None);
            Assert.Equal("synthetic-account", credential.AccountId);
            Assert.NotEqual("synthetic.token", credential.Fingerprint);
        }
        else
        {
            var exception = await Assert.ThrowsAsync<ProviderReadException>(() => new CodexCredentialReader().ReadAsync(source, CancellationToken.None));
            Assert.Equal(ErrorCategory.Schema, exception.Category);
            Assert.Equal("API-key authentication does not expose ChatGPT Codex quota", exception.Message);
        }
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

    [Fact]
    public void Usage_parser_reads_the_observed_live_response_layout()
    {
        // Structure mirrors a live chatgpt.com/backend-api/wham/usage response observed on 2026-09-07.
        // Values are synthetic and no account identifiers are stored in this repository.
        using var document = JsonDocument.Parse("""
            {"user_id":"synthetic","account_id":"synthetic","email":"synthetic","plan_type":"plus",
             "rate_limit":{"allowed":true,"limit_reached":false,
               "primary_window":{"used_percent":53,"limit_window_seconds":18000,"reset_after_seconds":3883,"reset_at":1900003600},
               "secondary_window":{"used_percent":8,"limit_window_seconds":604800,"reset_after_seconds":590683,"reset_at":1900590000}},
             "code_review_rate_limit":null,"additional_rate_limits":null,
             "model_usage":{"gpt-6-astra":{"available":true,"available_at":null}},
             "credits":{"has_credits":false,"balance":"0","approx_local_messages":[0,0]},
             "spend_control":{"reached":false},"rate_limit_reset_credits":{"available_count":1}}
            """);

        var windows = CodexUsageParser.Parse(document.RootElement, DateTimeOffset.FromUnixTimeSeconds(1900000000));

        Assert.Collection(windows,
            primary => { Assert.Equal("primary", primary.Id); Assert.Equal("5h limit", primary.Scope); Assert.Equal(.53m, primary.Limit.UsedFraction); Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1900003600), primary.ResetsAt); },
            secondary => { Assert.Equal("secondary", secondary.Id); Assert.Equal("Weekly limit", secondary.Scope); Assert.Equal(.08m, secondary.Limit.UsedFraction); });
    }

    [Fact]
    public void Usage_parser_accepts_legacy_window_aliases_without_retaining_response_data()
    {
        using var document = JsonDocument.Parse("""
            {"rate_limits":{"five_hour":{"limit_window_seconds":18000,"percent_left":87.5,"reset_time_ms":1800003600000},"weekly":{"limit_window_seconds":604800,"percent_left":25,"reset_at":"2027-01-15T12:00:00Z"}}}
            """);

        var windows = CodexUsageParser.Parse(document.RootElement, DateTimeOffset.FromUnixTimeSeconds(1800000000));

        Assert.Collection(windows,
            primary => { Assert.Equal("primary", primary.Id); Assert.Equal(.125m, primary.Limit.UsedFraction); Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1800003600000), primary.ResetsAt); },
            secondary => { Assert.Equal("secondary", secondary.Id); Assert.Equal(.75m, secondary.Limit.UsedFraction); Assert.Equal(DateTimeOffset.Parse("2027-01-15T12:00:00Z"), secondary.ResetsAt); });
    }

    [Fact]
    public void Usage_parser_accepts_a_data_wrapped_rate_limit()
    {
        using var document = JsonDocument.Parse("""
            {"data":{"rate_limits":{"five_hour":{"limit_window_seconds":18000,"percent_left":80}}}}
            """);

        var window = Assert.Single(CodexUsageParser.Parse(document.RootElement, DateTimeOffset.UtcNow));

        Assert.Equal("primary", window.Id);
        Assert.Equal(.2m, window.Limit.UsedFraction);
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
    public async Task Transport_failures_become_transient_errors_instead_of_escaping_the_adapter()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "auth.json"), "{\"tokens\":{\"access_token\":\"synthetic.token\",\"account_id\":\"synthetic-account\"}}", CancellationToken.None);
        var provider = CreateProvider(new ThrowingHandler(new HttpRequestException("connection refused (127.0.0.1:1)")));

        var exception = await Assert.ThrowsAsync<ProviderReadException>(() => provider.ReadAsync(new ProviderConnection(ProviderId.OpenAi, "test", true, 1), CancellationToken.None));

        Assert.True(exception.IsTransient);
        Assert.False(exception.IsSchemaFailure);
        Assert.Equal(ErrorCategory.Network, exception.Category);
        Assert.Equal("Provider request failed", exception.SafeMessage);
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
    [InlineData("{\"rate_limit\":{\"primary_window\":{\"used_percent\":40}}}")]
    public async Task Usage_request_turns_invalid_or_incomplete_bodies_into_safe_schema_failures(string body)
    {
        await WriteAuthAsync("token.one.signature", "account-private");
        var provider = CreateProvider(new TestHandler(_ => JsonResponse(body)));

        var exception = await Assert.ThrowsAsync<ProviderReadException>(() => provider.ReadAsync(Connection(), CancellationToken.None));

        Assert.Equal(ErrorCategory.Schema, exception.Category);
    }

    [Fact]
    public async Task Usage_request_preserves_a_null_percentage_as_an_honest_unavailable_reading()
    {
        await WriteAuthAsync("token.one.signature", "account-private");
        var provider = CreateProvider(new TestHandler(_ => JsonResponse("{\"rate_limit\":{\"primary_window\":{\"limit_window_seconds\":18000,\"used_percent\":null}}}")));

        var snapshot = await provider.ReadAsync(Connection(), CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.Null(snapshot.Headline?.Limit.UsedFraction);
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

public sealed class ClaudeUsageParserTests
{
    [Fact]
    public void Parser_merges_limits_with_named_windows_and_deduplicates_session()
    {
        using var document = JsonDocument.Parse("""
            {"limits":[{"kind":"session","percent":20,"resets_at":"2027-01-15T12:00:00Z"},{"kind":"weekly_opus","percent":70,"resets_at":"2027-01-20T12:00:00Z"}],"five_hour":{"utilization":90,"resets_at":"2027-01-15T13:00:00Z"},"seven_day":{"utilization":30,"resets_at":"2027-01-20T13:00:00Z"}}
            """);

        var windows = ClaudeUsageParser.Parse(document.RootElement, DateTimeOffset.UtcNow);

        Assert.Collection(windows,
            session => { Assert.Equal("session", session.Id); Assert.Equal(.2m, session.Limit.UsedFraction); },
            weeklyAll => { Assert.Equal("weekly_all", weeklyAll.Id); Assert.Equal(.3m, weeklyAll.Limit.UsedFraction); },
            opus => { Assert.Equal("weekly_opus", opus.Id); Assert.Equal(.7m, opus.Limit.UsedFraction); });
    }

    [Fact]
    public void Parser_keeps_a_named_window_with_no_reset_as_an_honest_reading()
    {
        using var document = JsonDocument.Parse("""{"five_hour":{"utilization":10}}""");

        var window = Assert.Single(ClaudeUsageParser.Parse(document.RootElement, DateTimeOffset.UtcNow));

        Assert.Equal("session", window.Id);
        Assert.Null(window.ResetsAt);
        Assert.Equal(.1m, window.Limit.UsedFraction);
    }

    [Fact]
    public void Parser_reads_the_observed_live_response_layout()
    {
        // Structure mirrors a Claude Code 2.1.263 /api/oauth/usage response observed on 2026-09-07.
        // Values are synthetic; no account data is stored in this repository.
        using var document = JsonDocument.Parse("""
            {"five_hour":{"utilization":20.0,"resets_at":"2027-01-15T12:00:00.000000+00:00","limit_dollars":null,"used_dollars":null,"locked_reason":null},
             "seven_day":{"utilization":16.0,"resets_at":"2027-01-20T12:00:00.000000+00:00","limit_dollars":null},
             "seven_day_opus":null,"nimbus_quill":{"utilization":0.0,"resets_at":null},
             "extra_usage":{"is_enabled":false,"utilization":null},
             "limits":[{"kind":"session","group":"session","percent":20,"severity":"normal","resets_at":"2027-01-15T12:00:00.000000+00:00","scope":null,"is_active":true},
                       {"kind":"weekly_all","group":"weekly","percent":17,"severity":"normal","resets_at":"2027-01-20T12:00:00.000000+00:00","scope":null,"is_active":false},
                       {"kind":"weekly_scoped","group":"weekly","percent":7,"severity":"normal","resets_at":"2027-01-20T12:00:00.000000+00:00","scope":{"model":{"id":null,"display_name":"Fable"},"surface":null},"is_active":false}],
             "spend":{"percent":0,"enabled":false},"member_dashboard_available":false}
            """);

        var windows = ClaudeUsageParser.Parse(document.RootElement, DateTimeOffset.UtcNow);

        Assert.Collection(windows,
            session => { Assert.Equal("session", session.Id); Assert.Equal(.2m, session.Limit.UsedFraction); Assert.NotNull(session.ResetsAt); },
            weeklyAll => { Assert.Equal("weekly_all", weeklyAll.Id); Assert.Equal(.17m, weeklyAll.Limit.UsedFraction); },
            scoped => { Assert.Equal("weekly_scoped:fable", scoped.Id); Assert.Equal("Fable", scoped.Scope); Assert.Equal(.07m, scoped.Limit.UsedFraction); });
    }

    [Fact]
    public void Parser_keeps_scoped_windows_distinct_instead_of_collapsing_them()
    {
        using var document = JsonDocument.Parse("""
            {"limits":[{"kind":"weekly_scoped","percent":7,"scope":{"model":{"display_name":"Fable"}}},
                       {"kind":"weekly_scoped","percent":41,"scope":{"model":{"display_name":"Opus"}}},
                       {"kind":"weekly_scoped","percent":3,"scope":null}]}
            """);

        var windows = ClaudeUsageParser.Parse(document.RootElement, DateTimeOffset.UtcNow);

        Assert.Equal(3, windows.Count);
        Assert.Equal(["weekly_scoped", "weekly_scoped:fable", "weekly_scoped:opus"], windows.Select(window => window.Id));
        Assert.Equal("Scoped model", windows.Single(window => window.Id == "weekly_scoped").Scope);
    }

    [Fact]
    public void Source_resolution_prefers_explicit_then_environment_then_profile()
    {
        var explicitRoot = Path.Combine(Path.GetTempPath(), "claude-explicit");
        var source = new ClaudeSourceResolver(explicitRoot, Path.Combine(Path.GetTempPath(), "claude-environment"), Path.Combine(Path.GetTempPath(), "claude-profile")).Resolve();

        Assert.Equal(Path.GetFullPath(explicitRoot), source.RootPath);
        Assert.StartsWith("claude:", source.SourceId, StringComparison.Ordinal);
    }
}

public sealed class ClaudeUsageProviderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "UseNotch.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Credential_reader_reports_a_missing_sign_in_when_no_file_exists()
    {
        var exception = await Assert.ThrowsAsync<ProviderReadException>(
            () => new ClaudeCredentialReader().ReadAsync(new ClaudeSource(_root, "test"), CancellationToken.None));

        Assert.Equal(401, exception.Code);
        Assert.Equal(AuthenticationState.Missing, exception.AuthenticationHint);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"ANTHROPIC_API_KEY\":\"synthetic\"}")]
    [InlineData("{\"claudeAiOauth\":{\"accessToken\":\"\"}}")]
    public async Task Credential_reader_reports_unsupported_shapes_without_retrying_forever(string body)
    {
        await WriteCredentialsAsync(body);

        var exception = await Assert.ThrowsAsync<ProviderReadException>(
            () => new ClaudeCredentialReader().ReadAsync(new ClaudeSource(_root, "test"), CancellationToken.None));

        Assert.True(exception.IsSchemaFailure);
        Assert.Equal(AuthenticationState.Unsupported, exception.AuthenticationHint);
    }

    [Fact]
    public async Task Credential_reader_recovers_from_a_partial_write_on_retry()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, ".credentials.json");
        await File.WriteAllTextAsync(path, "{\"claudeAiOauth\":{\"acce");
        _ = Task.Run(async () =>
        {
            await Task.Delay(20);
            await File.WriteAllTextAsync(path, "{\"claudeAiOauth\":{\"accessToken\":\"complete.token\",\"expiresAt\":1900000000000}}");
        });

        var credential = await new ClaudeCredentialReader().ReadAsync(new ClaudeSource(_root, "test"), CancellationToken.None);

        Assert.Equal("complete.token", credential.AccessToken);
    }

    [Fact]
    public async Task Credential_reader_keeps_the_expiry_hint_local_and_out_of_the_fingerprint()
    {
        await WriteCredentialsAsync("{\"claudeAiOauth\":{\"accessToken\":\"synthetic.token\",\"expiresAt\":1900000000000}}");

        var credential = await new ClaudeCredentialReader().ReadAsync(new ClaudeSource(_root, "test"), CancellationToken.None);

        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1900000000000), credential.ExpiryHint);
        Assert.NotEqual(credential.AccessToken, credential.Fingerprint);
    }

    [Fact]
    public async Task Usage_request_sends_only_to_the_allowlisted_endpoint_with_the_oauth_beta_header()
    {
        await WriteCredentialsAsync("{\"claudeAiOauth\":{\"accessToken\":\"synthetic.token\"}}");
        var handler = new TestHandler(request =>
        {
            Assert.Equal(ClaudeUsageProvider.UsageEndpoint, request.RequestUri);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("synthetic.token", request.Headers.Authorization?.Parameter);
            Assert.Equal("oauth-2025-04-20", request.Headers.GetValues("anthropic-beta").Single());
            return JsonResponse("""{"five_hour":{"utilization":40,"resets_at":"2027-01-15T12:00:00Z"}}""");
        });

        var snapshot = await CreateProvider(handler).ReadAsync(Connection(), CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.Equal("session", snapshot.HeadlineWindowId);
        Assert.Equal("Claude Code credentials", snapshot.Source.Kind);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Unauthorized_response_is_expired_when_the_local_hint_already_passed()
    {
        await WriteCredentialsAsync("{\"claudeAiOauth\":{\"accessToken\":\"synthetic.token\",\"expiresAt\":0}}");
        var provider = CreateProvider(new TestHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)));

        var exception = await Assert.ThrowsAsync<ProviderReadException>(() => provider.ReadAsync(Connection(), CancellationToken.None));

        Assert.Equal(AuthenticationState.Expired, exception.AuthenticationHint);
    }

    [Fact]
    public async Task Unauthorized_response_is_rejected_when_there_is_no_expired_local_hint()
    {
        await WriteCredentialsAsync("{\"claudeAiOauth\":{\"accessToken\":\"synthetic.token\"}}");
        var provider = CreateProvider(new TestHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)));

        var exception = await Assert.ThrowsAsync<ProviderReadException>(() => provider.ReadAsync(Connection(), CancellationToken.None));

        Assert.Equal(AuthenticationState.Rejected, exception.AuthenticationHint);
    }

    [Fact]
    public async Task Forbidden_response_reports_access_denied()
    {
        await WriteCredentialsAsync("{\"claudeAiOauth\":{\"accessToken\":\"synthetic.token\"}}");
        var provider = CreateProvider(new TestHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)));

        var exception = await Assert.ThrowsAsync<ProviderReadException>(() => provider.ReadAsync(Connection(), CancellationToken.None));

        Assert.Equal(ErrorCategory.Forbidden, exception.Category);
        Assert.Equal(AuthenticationState.AccessDenied, exception.AuthenticationHint);
    }

    [Fact]
    public async Task Unauthorized_request_retries_once_only_when_the_credential_file_token_rotates()
    {
        await WriteCredentialsAsync("{\"claudeAiOauth\":{\"accessToken\":\"token.one\"}}");
        var handler = new TestHandler(request =>
        {
            if (request.Headers.Authorization?.Parameter == "token.one")
            {
                File.WriteAllText(Path.Combine(_root, ".credentials.json"), "{\"claudeAiOauth\":{\"accessToken\":\"token.two\"}}");
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }
            return JsonResponse("""{"five_hour":{"utilization":40}}""");
        });

        var snapshot = await CreateProvider(handler).ReadAsync(Connection(), CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Unauthorized_request_does_not_retry_when_the_credential_file_is_unchanged()
    {
        await WriteCredentialsAsync("{\"claudeAiOauth\":{\"accessToken\":\"token.one\"}}");
        var handler = new TestHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        await Assert.ThrowsAsync<ProviderReadException>(() => CreateProvider(handler).ReadAsync(Connection(), CancellationToken.None));

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Transport_failures_become_transient_errors_instead_of_escaping_the_adapter()
    {
        await WriteCredentialsAsync("{\"claudeAiOauth\":{\"accessToken\":\"synthetic.token\"}}");
        var provider = CreateProvider(new ThrowingHandler(new HttpRequestException("connection refused (127.0.0.1:1)")));

        var exception = await Assert.ThrowsAsync<ProviderReadException>(() => provider.ReadAsync(Connection(), CancellationToken.None));

        Assert.True(exception.IsTransient);
        Assert.False(exception.IsSchemaFailure);
        Assert.Equal(ErrorCategory.Network, exception.Category);
        Assert.Equal("Provider request failed", exception.SafeMessage);
    }

    [Fact]
    public async Task Oversized_response_is_a_safe_schema_failure()
    {
        await WriteCredentialsAsync("{\"claudeAiOauth\":{\"accessToken\":\"synthetic.token\"}}");
        var body = "{\"five_hour\":{\"utilization\":40,\"padding\":\"" + new string('x', 300 * 1024) + "\"}}";
        var provider = CreateProvider(new TestHandler(_ => JsonResponse(body)));

        var exception = await Assert.ThrowsAsync<ProviderReadException>(() => provider.ReadAsync(Connection(), CancellationToken.None));

        Assert.True(exception.IsSchemaFailure);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    private ClaudeUsageProvider CreateProvider(HttpMessageHandler handler) => new(new ClaudeSourceResolver(_root), handler: handler);
    private static ProviderConnection Connection() => new(ProviderId.Anthropic, "test", true, 1);

    private async Task WriteCredentialsAsync(string body)
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, ".credentials.json"), body, CancellationToken.None);
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
}

internal sealed class ThrowingHandler(Exception failure) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => throw failure;
}
