using UseNotch.Domain;

namespace UseNotch.Application;

public enum OverlayEdge { Right, Left, Top, Bottom }

public sealed record ProviderSettings(bool Enabled, string? SelectedRoot)
{
    public static ProviderSettings Default { get; } = new(true, null);
}

public sealed record OverlaySettings(
    string? MonitorId,
    OverlayEdge Edge,
    int OffsetX,
    int OffsetY,
    bool Pinned,
    bool Visible,
    double UiScale,
    bool ReducedMotion,
    bool VisibleOverFullScreen)
{
    public const double MinimumScale = 0.75;
    public const double MaximumScale = 2.0;
    public const int MaximumOffset = 4000;

    public static OverlaySettings Default { get; } = new(null, OverlayEdge.Right, 0, 0, false, true, 1.0, false, false);
}

public sealed record PrivacySettings(bool ActivityMonitoringEnabled, bool DiagnosticsEnabled)
{
    // Diagnostics stay off unless the user asks for them, and they never record response bodies.
    public static PrivacySettings Default { get; } = new(true, false);
}

public sealed record UseNotchSettings(
    int SchemaVersion,
    ProviderSettings OpenAi,
    ProviderSettings Anthropic,
    OverlaySettings Overlay,
    PrivacySettings Privacy,
    bool LaunchAtLogin)
{
    public const int CurrentSchemaVersion = 1;

    public static UseNotchSettings Default { get; } = new(
        CurrentSchemaVersion,
        ProviderSettings.Default,
        ProviderSettings.Default,
        OverlaySettings.Default,
        PrivacySettings.Default,
        false);

    public ProviderSettings For(ProviderId provider) => provider == ProviderId.OpenAi ? OpenAi : Anthropic;

    public UseNotchSettings With(ProviderId provider, ProviderSettings settings)
        => provider == ProviderId.OpenAi ? this with { OpenAi = settings } : this with { Anthropic = settings };
}

public enum SettingsLoadOutcome { Defaults, Loaded, Migrated, Recovered }

public sealed record SettingsLoadResult(UseNotchSettings Settings, SettingsLoadOutcome Outcome, string? RecoveredFrom);

public interface ISettingsRepository
{
    Task<SettingsLoadResult> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(UseNotchSettings settings, CancellationToken cancellationToken);
}

public enum StartupRegistrationState { NotRegistered, RegisteredForThisApplication, RegisteredElsewhere, Unavailable }

public interface IStartupRegistration
{
    /// <summary>
    /// Reads the state that actually exists, so the UI can never claim a registration it does not have.
    /// </summary>
    StartupRegistrationState Read();

    bool TryRegister();

    bool TryUnregister();
}

public interface ISecretStore
{
    /// <summary>
    /// Protects small app-owned secret material for the current user. Borrowed provider tokens are never
    /// passed here; they exist only in memory for the lifetime of a request.
    /// </summary>
    byte[] Protect(byte[] plaintext);

    byte[]? TryUnprotect(byte[] protectedData);
}

public static class SettingsValidator
{
    /// <summary>
    /// Brings a loaded document into a usable shape without discarding the whole file over one bad value.
    /// Anything unusable falls back to its default rather than to an invented value.
    /// </summary>
    public static UseNotchSettings Normalize(UseNotchSettings settings) => new(
        UseNotchSettings.CurrentSchemaVersion,
        NormalizeProvider(settings.OpenAi),
        NormalizeProvider(settings.Anthropic),
        NormalizeOverlay(settings.Overlay),
        settings.Privacy ?? PrivacySettings.Default,
        settings.LaunchAtLogin);

    private static ProviderSettings NormalizeProvider(ProviderSettings? provider)
    {
        if (provider is null)
        {
            return ProviderSettings.Default;
        }

        var root = provider.SelectedRoot;
        if (string.IsNullOrWhiteSpace(root))
        {
            return provider with { SelectedRoot = null };
        }

        try
        {
            // A selected root must already be absolute. Resolving a relative value would silently bind it
            // to whatever the working directory happens to be, which is not a location the user chose.
            var trimmed = root.Trim();
            return provider with { SelectedRoot = Path.IsPathFullyQualified(trimmed) ? Path.GetFullPath(trimmed) : null };
        }
        catch (ArgumentException)
        {
            return provider with { SelectedRoot = null };
        }
        catch (NotSupportedException)
        {
            return provider with { SelectedRoot = null };
        }
        catch (PathTooLongException)
        {
            return provider with { SelectedRoot = null };
        }
    }

    private static OverlaySettings NormalizeOverlay(OverlaySettings? overlay)
    {
        if (overlay is null)
        {
            return OverlaySettings.Default;
        }

        var scale = double.IsFinite(overlay.UiScale)
            ? Math.Clamp(overlay.UiScale, OverlaySettings.MinimumScale, OverlaySettings.MaximumScale)
            : OverlaySettings.Default.UiScale;
        return overlay with
        {
            MonitorId = string.IsNullOrWhiteSpace(overlay.MonitorId) ? null : overlay.MonitorId.Trim(),
            Edge = Enum.IsDefined(overlay.Edge) ? overlay.Edge : OverlaySettings.Default.Edge,
            OffsetX = Math.Clamp(overlay.OffsetX, -OverlaySettings.MaximumOffset, OverlaySettings.MaximumOffset),
            OffsetY = Math.Clamp(overlay.OffsetY, -OverlaySettings.MaximumOffset, OverlaySettings.MaximumOffset),
            UiScale = scale,
        };
    }
}
