namespace UseNotch.Platform.Windows.Overlay;

public interface IOverlayMonitorService
{
    IReadOnlyList<DisplayMonitor> GetMonitors();
}

public interface IOverlayWindowPlatform : IDisposable
{
    nint GetForegroundWindow();

    void Attach(
        nint windowHandle,
        Func<OverlayRegionSnapshot> interactiveRegionProvider,
        Action nativeMetricsChanged,
        Action<bool>? cursorInsideChanged = null);

    void UpdateInteractiveRegions();
}
