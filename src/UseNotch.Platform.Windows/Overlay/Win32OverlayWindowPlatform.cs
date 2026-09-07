using System.Runtime.InteropServices;

namespace UseNotch.Platform.Windows.Overlay;

public sealed class Win32OverlayWindowPlatform : IOverlayWindowPlatform
{
    private const int ExtendedStyleIndex = -20;
    private const int WindowProcedureIndex = -4;
    private const long ExtendedStyleNoActivate = 0x08000000L;
    private const long ExtendedStyleToolWindow = 0x00000080L;
    private const long ExtendedStyleTransparent = 0x00000020L;
    private const long ExtendedStyleLayered = 0x00080000L;
    private const uint SetWindowPosNoSize = 0x0001;
    private const uint SetWindowPosNoMove = 0x0002;
    private const uint SetWindowPosNoActivate = 0x0010;
    private const uint SetWindowPosFrameChanged = 0x0020;
    private const uint SetWindowPosNoZOrder = 0x0004;
    private const int HitTestMessage = 0x0084;
    private const int MouseActivateMessage = 0x0021;
    private const int DpiChangedMessage = 0x02E0;
    private const int DisplayChangedMessage = 0x007E;
    private const int SettingChangedMessage = 0x001A;
    private const int HitTestTransparent = -1;
    private const int HitTestClient = 1;
    private const int MouseActivateNoActivate = 3;

    // Polling the cursor is what makes cross-process click-through possible at all. See ApplyClickThrough.
    private static readonly TimeSpan CursorPollInterval = TimeSpan.FromMilliseconds(50);

    private readonly WindowProcedure _windowProcedure;
    private readonly object _gate = new();
    private nint _windowHandle;
    private nint _originalWindowProcedure;
    private Func<OverlayRegionSnapshot>? _interactiveRegionProvider;
    private Action? _nativeMetricsChanged;
    private Timer? _cursorPoll;
    private IReadOnlyList<PixelRect> _cachedRegions = [];
    private bool _clickThroughEnabled;
    private bool _disposed;

    public Win32OverlayWindowPlatform() => _windowProcedure = WindowProcedureCallback;

    public nint GetForegroundWindow() => NativeGetForegroundWindow();

    public void Attach(
        nint windowHandle,
        Func<OverlayRegionSnapshot> interactiveRegionProvider,
        Action nativeMetricsChanged)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfZero(windowHandle);
        ArgumentNullException.ThrowIfNull(interactiveRegionProvider);
        ArgumentNullException.ThrowIfNull(nativeMetricsChanged);
        if (_windowHandle != nint.Zero)
        {
            throw new InvalidOperationException("The native overlay behavior is already attached.");
        }

        _windowHandle = windowHandle;
        _interactiveRegionProvider = interactiveRegionProvider;
        _nativeMetricsChanged = nativeMetricsChanged;

        var extendedStyle = GetWindowLongPtr(windowHandle, ExtendedStyleIndex).ToInt64();
        // WS_EX_LAYERED is what makes WS_EX_TRANSPARENT actually forward mouse input to the window
        // underneath, including one owned by another process.
        SetWindowLongPtrChecked(windowHandle, ExtendedStyleIndex,
            new nint(extendedStyle | ExtendedStyleNoActivate | ExtendedStyleToolWindow | ExtendedStyleLayered));

        var procedurePointer = Marshal.GetFunctionPointerForDelegate(_windowProcedure);
        _originalWindowProcedure = SetWindowLongPtrChecked(windowHandle, WindowProcedureIndex, procedurePointer);
        _ = SetWindowPos(
            windowHandle,
            new nint(-1),
            0,
            0,
            0,
            0,
            SetWindowPosNoSize | SetWindowPosNoMove | SetWindowPosNoActivate | SetWindowPosFrameChanged);
        UpdateInteractiveRegions();
        _cursorPoll = new Timer(_ => ApplyClickThrough(), null, CursorPollInterval, CursorPollInterval);
    }

    /// <summary>
    /// Refreshes the cached interactive geometry. This must run on the UI thread, because the provider
    /// reads the layout of the overlay window, and the cursor poll then only reads the cached result.
    /// </summary>
    public void UpdateInteractiveRegions()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_interactiveRegionProvider is null || _windowHandle == nint.Zero)
        {
            return;
        }

        var clientRect = new NativeRect();
        var regions = GetClientRect(_windowHandle, ref clientRect)
            ? OverlayRegionScaler.ToClientPixels(
                _interactiveRegionProvider(),
                new PixelSize(clientRect.Right - clientRect.Left, clientRect.Bottom - clientRect.Top))
            : [];
        lock (_gate)
        {
            _cachedRegions = regions;
        }

        ApplyClickThrough();
    }

    /// <summary>
    /// Returning HTTRANSPARENT only forwards a click to another window owned by the same thread, so it
    /// cannot hand input to a different application. WS_EX_TRANSPARENT can, but it applies to the whole
    /// window, so it is toggled from the cursor position: the overlay is click-through everywhere except
    /// while the pointer is actually over one of its visible controls.
    /// </summary>
    private void ApplyClickThrough()
    {
        if (_disposed || _windowHandle == nint.Zero)
        {
            return;
        }

        var shouldBeClickThrough = !IsCursorOverInteractiveRegion();
        if (shouldBeClickThrough == _clickThroughEnabled)
        {
            return;
        }

        var extendedStyle = GetWindowLongPtr(_windowHandle, ExtendedStyleIndex).ToInt64();
        var updated = shouldBeClickThrough
            ? extendedStyle | ExtendedStyleTransparent
            : extendedStyle & ~ExtendedStyleTransparent;
        if (updated != extendedStyle)
        {
            _ = SetWindowLongPtr(_windowHandle, ExtendedStyleIndex, new nint(updated));
            // An extended-style change only takes effect for hit testing once the frame is revalidated.
            _ = SetWindowPos(
                _windowHandle,
                nint.Zero,
                0,
                0,
                0,
                0,
                SetWindowPosNoSize | SetWindowPosNoMove | SetWindowPosNoZOrder | SetWindowPosNoActivate | SetWindowPosFrameChanged);
        }

        _clickThroughEnabled = shouldBeClickThrough;
    }

    private bool IsCursorOverInteractiveRegion()
    {
        IReadOnlyList<PixelRect> regions;
        lock (_gate)
        {
            regions = _cachedRegions;
        }

        if (regions.Count == 0 || !GetCursorPos(out var cursor) || !ScreenToClient(_windowHandle, ref cursor))
        {
            return false;
        }

        var point = new PixelPoint(cursor.X, cursor.Y);
        return regions.Any(region => region.Contains(point));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cursorPoll?.Dispose();
        _cursorPoll = null;
        if (_windowHandle != nint.Zero && _originalWindowProcedure != nint.Zero)
        {
            _ = SetWindowLongPtr(_windowHandle, WindowProcedureIndex, _originalWindowProcedure);
        }

        _windowHandle = nint.Zero;
        _originalWindowProcedure = nint.Zero;
        _interactiveRegionProvider = null;
        _nativeMetricsChanged = null;
    }

    private nint WindowProcedureCallback(nint windowHandle, uint message, nint wParam, nint lParam)
    {
        switch (message)
        {
            case HitTestMessage:
                // The cursor poll has already made the window click-through when the pointer is outside a
                // control. This keeps the precise per-region answer for everything else.
                return IsInteractiveScreenPoint(lParam) ? new nint(HitTestClient) : new nint(HitTestTransparent);
            case MouseActivateMessage:
                return new nint(MouseActivateNoActivate);
            case DpiChangedMessage:
            case DisplayChangedMessage:
            case SettingChangedMessage:
                _nativeMetricsChanged?.Invoke();
                break;
        }

        return CallWindowProc(_originalWindowProcedure, windowHandle, message, wParam, lParam);
    }

    private bool IsInteractiveScreenPoint(nint lParam)
    {
        if (_interactiveRegionProvider is null)
        {
            return false;
        }

        var screenPoint = new NativePoint
        {
            X = unchecked((short)((long)lParam & 0xffff)),
            Y = unchecked((short)(((long)lParam >> 16) & 0xffff)),
        };
        if (!ScreenToClient(_windowHandle, ref screenPoint))
        {
            return false;
        }

        var clientPoint = new PixelPoint(screenPoint.X, screenPoint.Y);
        IReadOnlyList<PixelRect> regions;
        lock (_gate)
        {
            regions = _cachedRegions;
        }

        return regions.Any(region => region.Contains(clientPoint));
    }

    private static nint SetWindowLongPtrChecked(nint windowHandle, int index, nint value)
    {
        Marshal.SetLastPInvokeError(0);
        var result = SetWindowLongPtr(windowHandle, index, value);
        if (result == nint.Zero && Marshal.GetLastPInvokeError() != 0)
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
        }

        return result;
    }

    private delegate nint WindowProcedure(nint windowHandle, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr(nint windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint windowHandle, int index, nint value);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint CallWindowProc(nint previousWindowProcedure, nint windowHandle, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(nint windowHandle, nint insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ScreenToClient(nint windowHandle, ref NativePoint point);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetClientRect(nint windowHandle, ref NativeRect rectangle);

    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")]
    private static extern nint NativeGetForegroundWindow();

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
