using UseNotch.App.ViewModels;
using UseNotch.Application;
using UseNotch.Domain;
using UseNotch.Infrastructure;
using UseNotch.Providers.Anthropic;
using UseNotch.Providers.OpenAI;

namespace UseNotch.App.Lifecycle;

/// <summary>
/// Connects the settings window to the running coordinators. Every operation here is one the user asked
/// for; none of them writes to a provider's own installation.
/// </summary>
public sealed class AppSettingsRuntime(
    IUsageStateStore store,
    PollingCoordinator? polling,
    ActivityCoordinator? activity,
    Action<OverlaySettings>? applyOverlay = null) : ISettingsRuntime
{
    private long _generation = 1;

    public ProviderRuntimeState? GetState(ProviderId provider) => store.Get(provider);

    /// <summary>
    /// Names the resolved credential source. Environment variables set inside a terminal can differ from
    /// the environment a tray application inherits, so the resolved root is shown rather than assumed.
    /// </summary>
    public string DescribeSource(ProviderId provider)
    {
        try
        {
            return provider == ProviderId.OpenAi
                ? "Codex source: " + new CodexSourceResolver(inheritedCodexHome: Environment.GetEnvironmentVariable("CODEX_HOME")).Resolve().RootPath
                : "Claude Code source: " + new ClaudeSourceResolver(inheritedClaudeConfigDir: Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR")).Resolve().RootPath;
        }
        catch (ArgumentException)
        {
            return "Source could not be resolved";
        }
        catch (IOException)
        {
            return "Source could not be resolved";
        }
    }

    public void RequestRefresh(ProviderId provider) => polling?.RequestRefresh(provider, RefreshReason.Manual);

    public void SetPaused(bool paused)
    {
        polling?.SetPaused(paused);
        activity?.SetPaused(paused);
    }

    public async Task SetProviderEnabledAsync(ProviderId provider, bool enabled)
    {
        if (enabled)
        {
            if (polling is not null)
            {
                await polling.StartAsync(new ProviderConnection(provider, Source(provider), true, Interlocked.Increment(ref _generation))).ConfigureAwait(false);
            }

            activity?.Start(provider);
            return;
        }

        // Disconnecting stops this provider's file and network access and drops its state, so a disabled
        // provider cannot keep showing a reading.
        if (polling is not null)
        {
            await polling.DisconnectAsync(provider).ConfigureAwait(false);
        }

        if (activity is not null)
        {
            await activity.DisconnectAsync(provider).ConfigureAwait(false);
        }
    }

    public async Task ReconnectAsync(ProviderId provider)
    {
        await SetProviderEnabledAsync(provider, false).ConfigureAwait(false);
        await SetProviderEnabledAsync(provider, true).ConfigureAwait(false);
    }

    public IReadOnlyList<string> ClearOwnedData() => ApplicationPaths.ClearOwnedData();

    public string DescribeDataLocation() => ApplicationPaths.Root;

    public void ApplyOverlaySettings(OverlaySettings settings) => applyOverlay?.Invoke(settings);

    private static string Source(ProviderId provider) => provider == ProviderId.OpenAi ? "codex:default" : "claude:default";
}
