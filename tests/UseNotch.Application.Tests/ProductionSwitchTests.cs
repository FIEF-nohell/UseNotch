using UseNotch.Domain;
using UseNotch.TestSupport;

namespace UseNotch.Application.Tests;

/// <summary>
/// Guards the switches a production run must not take on its own: no implicit mock data, no implicit
/// renderer change, and no diagnostic output unless the user asked for it.
/// </summary>
public class ProductionSwitchTests
{
    private static string ReadSource(params string[] parts) => File.ReadAllText(RepositoryFiles.PathTo(parts));

    [Fact]
    public void Mock_scenarios_are_inert_unless_a_development_switch_selects_one()
    {
        // The selector defaults to null, so nothing in a normal run can put fixture data on screen.
        Assert.Null(MockScenarioCatalog.Create(ProviderId.OpenAi, MockScenario.Loading).Snapshot);

        var app = ReadSource("src", "UseNotch.App", "App.axaml.cs");
        Assert.Contains("--mock-scenario=", app, StringComparison.Ordinal);
        Assert.Contains("DevelopmentScenario = scenario", app, StringComparison.Ordinal);
    }

    [Fact]
    public void The_software_renderer_is_opt_in_so_a_normal_run_keeps_hardware_rendering()
    {
        var program = ReadSource("src", "UseNotch.App", "Program.cs");

        Assert.Contains("--software-render", program, StringComparison.Ordinal);
        Assert.Contains("Win32RenderingMode.Software", program, StringComparison.Ordinal);
        // The rendering mode is set inside the switch check, never unconditionally.
        var switchIndex = program.IndexOf("--software-render", StringComparison.Ordinal);
        var renderingIndex = program.IndexOf("Win32RenderingMode.Software", StringComparison.Ordinal);
        Assert.True(renderingIndex > switchIndex, "Software rendering is configured before the switch is checked.");
    }

    [Fact]
    public void Diagnostics_stay_off_until_the_stored_privacy_setting_turns_them_on()
    {
        Assert.False(PrivacySettings.Default.DiagnosticsEnabled);

        var app = ReadSource("src", "UseNotch.App", "App.axaml.cs");
        Assert.Contains("_diagnostics.IsEnabled = settings.Privacy.DiagnosticsEnabled", app, StringComparison.Ordinal);
    }

    [Fact]
    public void No_production_source_carries_a_credential_shaped_literal()
    {
        var sources = Directory.GetFiles(RepositoryFiles.PathTo("src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(sources);
        Assert.All(sources, path =>
        {
            var text = File.ReadAllText(path);
            foreach (var pattern in new[] { "sk-ant-", "sk-proj-", "sk_live_", "eyJhbGciOi" })
            {
                Assert.DoesNotContain(pattern, text, StringComparison.Ordinal);
            }
        });
    }

    [Fact]
    public void The_activity_cadence_matches_the_documented_intervals()
    {
        var options = ActivityOptions.Default;

        Assert.Equal(TimeSpan.FromSeconds(2), options.Interval);
        Assert.Equal(TimeSpan.FromSeconds(5), options.IdleInterval);
        Assert.Equal(TimeSpan.FromSeconds(8), options.EstimatedExpiry);
        Assert.Equal(TimeSpan.FromMilliseconds(150), options.Debounce);
    }

    [Fact]
    public void The_polling_cadence_matches_the_documented_intervals()
    {
        var options = PollingOptions.Default;

        Assert.Equal(TimeSpan.FromMinutes(1), options.ActiveInterval);
        Assert.Equal(TimeSpan.FromMinutes(5), options.IdleInterval);
        Assert.Equal(TimeSpan.FromSeconds(15), options.AttemptBudget);
        Assert.Equal(TimeSpan.FromSeconds(35), options.OperationBudget);
    }
}
