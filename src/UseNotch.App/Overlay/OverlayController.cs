using Avalonia;
using Avalonia.Threading;
using UseNotch.App.Views;
using UseNotch.Platform.Windows.Overlay;

namespace UseNotch.App.Overlay;

public sealed class OverlayController : IDisposable
{
    private readonly IOverlayMonitorService _monitorService;
    private readonly IOverlayWindowPlatform _windowPlatform;
    private readonly Func<OverlayWindow> _windowFactory;
    private OverlayWindow? _window;
    private bool _disposed;

    public OverlayController(
        IOverlayMonitorService monitorService,
        IOverlayWindowPlatform windowPlatform,
        Func<OverlayWindow> windowFactory)
    {
        _monitorService = monitorService;
        _windowPlatform = windowPlatform;
        _windowFactory = windowFactory;
    }

    public OverlayEdge Edge { get; set; } = OverlayEdge.Right;

    public string? PreferredMonitorId { get; set; }

    public nint ForegroundWindowBeforeShow { get; private set; }

    public nint ForegroundWindowAfterShow { get; private set; }

    public bool IsVisible => _window?.IsVisible == true;

    public void Show()
    {
        ThrowIfDisposed();
        var window = EnsureWindow();
        Reposition();

        if (window.IsVisible)
        {
            _windowPlatform.UpdateInteractiveRegions();
            return;
        }

        ForegroundWindowBeforeShow = _windowPlatform.GetForegroundWindow();
        window.Opacity = 0;
        window.Show();
        var handle = window.TryGetPlatformHandle()?.Handle ?? nint.Zero;
        if (handle == nint.Zero)
        {
            window.Hide();
            throw new InvalidOperationException("Avalonia did not provide an HWND for the overlay.");
        }

        window.UpdateLayout();
        _windowPlatform.Attach(
            handle,
            window.GetInteractiveRegions,
            OnNativeMetricsChanged);
        window.Opacity = 1;
        ForegroundWindowAfterShow = _windowPlatform.GetForegroundWindow();
        Dispatcher.UIThread.Post(
            _windowPlatform.UpdateInteractiveRegions,
            DispatcherPriority.Render);
    }

    public void Hide()
    {
        ThrowIfDisposed();
        _window?.Hide();
    }

    public void Toggle()
    {
        if (IsVisible)
        {
            Hide();
        }
        else
        {
            Show();
        }
    }

    public void Reposition()
    {
        ThrowIfDisposed();
        var window = EnsureWindow();
        var monitor = OverlayPlacementCalculator.SelectMonitor(
            _monitorService.GetMonitors(),
            PreferredMonitorId);
        var placement = OverlayPlacementCalculator.Calculate(
            monitor,
            Edge,
            new DipSize(window.Width, window.Height));
        window.Position = new Avalonia.PixelPoint(placement.Position.X, placement.Position.Y);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_window is not null)
        {
            _window.InteractiveRegionsChanged -= OnInteractiveRegionsChanged;
            _window.CloseForShutdown();
            _window = null;
        }

        _windowPlatform.Dispose();
    }

    private OverlayWindow EnsureWindow()
    {
        if (_window is not null)
        {
            return _window;
        }

        _window = _windowFactory();
        _window.InteractiveRegionsChanged += OnInteractiveRegionsChanged;
        return _window;
    }

    private void OnInteractiveRegionsChanged(object? sender, EventArgs e)
    {
        if (IsVisible)
        {
            _windowPlatform.UpdateInteractiveRegions();
        }
    }

    private void OnNativeMetricsChanged() =>
        Dispatcher.UIThread.Post(() =>
        {
            if (!_disposed)
            {
                Reposition();
                _windowPlatform.UpdateInteractiveRegions();
            }
        });

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
