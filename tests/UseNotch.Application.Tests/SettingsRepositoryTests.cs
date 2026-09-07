using System.Text.Json;
using UseNotch.Application;
using UseNotch.Domain;
using UseNotch.Infrastructure;

namespace UseNotch.Application.Tests;

public sealed class SettingsRepositoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "UseNotch.Tests", Guid.NewGuid().ToString("N"));

    private JsonSettingsRepository CreateRepository() => new(_root);

    [Fact]
    public async Task A_missing_document_yields_defaults_without_creating_one()
    {
        var result = await CreateRepository().LoadAsync(CancellationToken.None);

        Assert.Equal(SettingsLoadOutcome.Defaults, result.Outcome);
        Assert.Equal(UseNotchSettings.Default, result.Settings);
        Assert.False(File.Exists(Path.Combine(_root, JsonSettingsRepository.FileName)));
    }

    [Fact]
    public async Task Saved_settings_round_trip()
    {
        var repository = CreateRepository();
        var settings = UseNotchSettings.Default with
        {
            Overlay = OverlaySettings.Default with { Edge = OverlayEdge.Bottom, OffsetY = 40, UiScale = 1.25 },
            Privacy = new PrivacySettings(false, true),
            LaunchAtLogin = true,
        };

        await repository.SaveAsync(settings, CancellationToken.None);
        var loaded = await repository.LoadAsync(CancellationToken.None);

        Assert.Equal(SettingsLoadOutcome.Loaded, loaded.Outcome);
        Assert.Equal(OverlayEdge.Bottom, loaded.Settings.Overlay.Edge);
        Assert.Equal(40, loaded.Settings.Overlay.OffsetY);
        Assert.Equal(1.25, loaded.Settings.Overlay.UiScale);
        Assert.True(loaded.Settings.LaunchAtLogin);
        Assert.True(loaded.Settings.Privacy.DiagnosticsEnabled);
        Assert.False(loaded.Settings.Privacy.ActivityMonitoringEnabled);
    }

    [Fact]
    public async Task An_older_schema_version_is_migrated_rather_than_discarded()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(
            Path.Combine(_root, JsonSettingsRepository.FileName),
            """{"SchemaVersion":0,"OpenAi":{"Enabled":false,"SelectedRoot":null},"Anthropic":{"Enabled":true,"SelectedRoot":null},"Overlay":{"Edge":"Left","OffsetX":10,"OffsetY":0,"Pinned":false,"Visible":true,"UiScale":1.0,"ReducedMotion":false,"VisibleOverFullScreen":false},"Privacy":{"ActivityMonitoringEnabled":true,"DiagnosticsEnabled":false},"LaunchAtLogin":false}""");

        var loaded = await CreateRepository().LoadAsync(CancellationToken.None);

        Assert.Equal(SettingsLoadOutcome.Migrated, loaded.Outcome);
        Assert.Equal(UseNotchSettings.CurrentSchemaVersion, loaded.Settings.SchemaVersion);
        Assert.False(loaded.Settings.OpenAi.Enabled);
        Assert.Equal(OverlayEdge.Left, loaded.Settings.Overlay.Edge);
    }

    [Fact]
    public async Task An_unreadable_document_is_kept_beside_the_recovered_defaults()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, JsonSettingsRepository.FileName);
        await File.WriteAllTextAsync(path, "{ this is not json");

        var loaded = await CreateRepository().LoadAsync(CancellationToken.None);

        Assert.Equal(SettingsLoadOutcome.Recovered, loaded.Outcome);
        Assert.Equal(UseNotchSettings.Default, loaded.Settings);
        Assert.True(File.Exists(path + ".invalid"));
    }

    [Fact]
    public async Task A_save_replaces_the_document_atomically_and_leaves_no_temporary_file()
    {
        var repository = CreateRepository();

        await repository.SaveAsync(UseNotchSettings.Default, CancellationToken.None);
        await repository.SaveAsync(UseNotchSettings.Default with { LaunchAtLogin = true }, CancellationToken.None);

        Assert.Empty(Directory.GetFiles(_root, "*.tmp-*"));
        Assert.Single(Directory.GetFiles(_root, JsonSettingsRepository.FileName));
    }

    [Fact]
    public async Task No_secret_material_is_written_to_the_settings_document()
    {
        var repository = CreateRepository();

        await repository.SaveAsync(UseNotchSettings.Default, CancellationToken.None);
        var text = await File.ReadAllTextAsync(Path.Combine(_root, JsonSettingsRepository.FileName));

        foreach (var forbidden in new[] { "token", "secret", "password", "credential", "Bearer" })
        {
            Assert.DoesNotContain(forbidden, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData("relative/path")]
    [InlineData("   ")]
    public void A_selected_root_that_is_not_an_absolute_local_path_is_dropped(string root)
    {
        var normalized = SettingsValidator.Normalize(UseNotchSettings.Default with { OpenAi = new ProviderSettings(true, root) });

        Assert.Null(normalized.OpenAi.SelectedRoot);
    }

    [Fact]
    public void An_absolute_selected_root_survives_validation()
    {
        var normalized = SettingsValidator.Normalize(UseNotchSettings.Default with { Anthropic = new ProviderSettings(true, @"C:\fixture\.claude") });

        Assert.Equal(@"C:\fixture\.claude", normalized.Anthropic.SelectedRoot);
    }

    [Theory]
    [InlineData(0.1, OverlaySettings.MinimumScale)]
    [InlineData(9.0, OverlaySettings.MaximumScale)]
    [InlineData(double.NaN, 1.0)]
    public void An_unusable_scale_falls_back_instead_of_breaking_the_whole_document(double stored, double expected)
    {
        var normalized = SettingsValidator.Normalize(UseNotchSettings.Default with { Overlay = OverlaySettings.Default with { UiScale = stored } });

        Assert.Equal(expected, normalized.Overlay.UiScale);
    }

    [Fact]
    public void An_undefined_edge_falls_back_to_the_default_edge()
    {
        var normalized = SettingsValidator.Normalize(UseNotchSettings.Default with { Overlay = OverlaySettings.Default with { Edge = (OverlayEdge)99 } });

        Assert.Equal(OverlaySettings.Default.Edge, normalized.Overlay.Edge);
    }

    [Fact]
    public void An_out_of_range_offset_is_clamped()
    {
        var normalized = SettingsValidator.Normalize(UseNotchSettings.Default with { Overlay = OverlaySettings.Default with { OffsetX = 100000, OffsetY = -100000 } });

        Assert.Equal(OverlaySettings.MaximumOffset, normalized.Overlay.OffsetX);
        Assert.Equal(-OverlaySettings.MaximumOffset, normalized.Overlay.OffsetY);
    }

    [Fact]
    public async Task Enum_values_are_stored_by_name_so_the_document_stays_readable_across_versions()
    {
        var repository = CreateRepository();

        await repository.SaveAsync(UseNotchSettings.Default with { Overlay = OverlaySettings.Default with { Edge = OverlayEdge.Top } }, CancellationToken.None);
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(_root, JsonSettingsRepository.FileName)));

        Assert.Equal("Top", document.RootElement.GetProperty("Overlay").GetProperty("Edge").GetString());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }
}

public sealed class DiagnosticSanitizerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "UseNotch.Tests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("token sk-ant-api03-abcdefghijklmnop")]
    [InlineData("header Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.payload.signature")]
    [InlineData("account someone@example.com")]
    [InlineData(@"path C:\Users\someone\private-project\notes.txt")]
    public void Sensitive_fragments_never_survive_sanitization(string message)
    {
        var sanitized = DiagnosticSanitizer.Sanitize(message);

        foreach (var forbidden in new[] { "sk-ant-api03-abcdefghijklmnop", "eyJhbGciOiJIUzI1NiJ9", "someone@example.com", "private-project" })
        {
            Assert.DoesNotContain(forbidden, sanitized, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_long_line_is_truncated_and_newlines_cannot_forge_extra_entries()
    {
        var sanitized = DiagnosticSanitizer.Sanitize(new string('x', 4000) + "\nforged entry");

        Assert.Equal(DiagnosticSanitizer.MaximumLineLength, sanitized.Length);
        Assert.DoesNotContain('\n', sanitized);
    }

    [Fact]
    public void Diagnostics_are_off_until_the_user_enables_them()
    {
        var log = new SanitizedDiagnosticLog(_root);

        log.Write("something happened");

        Assert.False(File.Exists(log.Path));
    }

    [Fact]
    public void An_enabled_log_writes_only_sanitized_lines_and_stays_bounded()
    {
        var log = new SanitizedDiagnosticLog(_root, maximumBytes: 512) { IsEnabled = true };

        for (var index = 0; index < 40; index++)
        {
            log.Write("attempt failed for someone@example.com with Bearer eyJhbGciOiJIUzI1NiJ9.abc.def");
        }

        var text = File.ReadAllText(log.Path);
        Assert.DoesNotContain("someone@example.com", text, StringComparison.Ordinal);
        Assert.DoesNotContain("eyJhbGciOiJIUzI1NiJ9", text, StringComparison.Ordinal);
        Assert.True(new FileInfo(log.Path).Length <= 4096);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }
}
