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
        Func<IReadOnlyList<PixelRect>> interactiveRegionProvider,
        Action nativeMetricsChanged);

    void UpdateInteractiveRegions();
}
