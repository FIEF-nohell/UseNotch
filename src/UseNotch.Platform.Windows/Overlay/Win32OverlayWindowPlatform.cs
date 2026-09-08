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
    // The fast cadence only applies while the pointer is near the overlay; away from it, a much slower
    // poll is enough and keeps an idle machine idle.
    private static readonly TimeSpan NearCursorPollInterval = TimeSpan.FromMilliseconds(50);
    // The far interval is the worst case for noticing the pointer at all: the fast interval only
    // applies once a poll has already seen the pointer nearby. At 400ms a deliberate move to the
    // edge could sit unnoticed for most of a second, which read as the overlay ignoring the
    // pointer. Reading the cursor position is a cheap syscall, so paying it more often is fair.
    private static readonly TimeSpan FarCursorPollInterval = TimeSpan.FromMilliseconds(150);
    private const int NearWindowMargin = 64;

    private readonly WindowProcedure _windowProcedure;
    private readonly object _gate = new();
    private nint _windowHandle;
    private nint _originalWindowProcedure;
    private Func<OverlayRegionSnapshot>? _interactiveRegionProvider;
    private Action? _nativeMetricsChanged;
    private Action<bool>? _cursorInsideChanged;
    private Timer? _cursorPoll;
    private IReadOnlyList<PixelRect> _cachedRegions = [];
    private IReadOnlyList<PixelRect> _cachedHoverRegions = [];
    private bool _cursorInsideHover;
    private bool _clickThroughEnabled;
    private volatile bool _cursorIsNearWindow;
    private bool _disposed;

    public Win32OverlayWindowPlatform() => _windowProcedure = WindowProcedureCallback;

    public nint GetForegroundWindow() => NativeGetForegroundWindow();

    public void Attach(
        nint windowHandle,
        Func<OverlayRegionSnapshot> interactiveRegionProvider,
        Action nativeMetricsChanged,
        Action<bool>? cursorInsideChanged = null)
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
        _cursorInsideChanged = cursorInsideChanged;

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
        _cursorPoll = new Timer(_ => PollCursor(), null, FarCursorPollInterval, Timeout.InfiniteTimeSpan);
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
        var snapshot = _interactiveRegionProvider();
        if (!GetClientRect(_windowHandle, ref clientRect))
        {
            // The client rect can fail transiently while the window is being restyled. The two caches
            // are treated differently on that failure, because the safe answer differs.
            //
            // Interactive regions are cleared: with none, the window is click-through, so a failure here
            // can never capture input the overlay does not own.
            //
            // Hover regions are kept. Emptying them says the pointer is outside the overlay, which
            // collapsed it under a pointer that had not moved and then reopened it on the next poll.
            // The last known regions are stale for at most one poll; claiming the overlay occupies
            // nothing is wrong immediately.
            lock (_gate)
            {
                _cachedRegions = [];
            }

            ApplyClickThrough();
            return;
        }

        var clientSize = new PixelSize(clientRect.Right - clientRect.Left, clientRect.Bottom - clientRect.Top);
        var regions = OverlayRegionScaler.ToClientPixels(snapshot.Regions, snapshot.ClientSize, clientSize);
        var hoverRegions = OverlayRegionScaler.ToClientPixels(snapshot.HoverRegions, snapshot.ClientSize, clientSize);

        lock (_gate)
        {
            _cachedRegions = regions;
            _cachedHoverRegions = hoverRegions;
        }

        ApplyClickThrough();
    }

    /// <summary>
    /// Returning HTTRANSPARENT only forwards a click to another window owned by the same thread, so it
    /// cannot hand input to a different application. WS_EX_TRANSPARENT can, but it applies to the whole
    /// window, so it is toggled from the cursor position: the overlay is click-through everywhere except
    /// while the pointer is actually over one of its visible controls.
    /// </summary>
    /// <summary>
    /// Reschedules itself instead of running on a fixed cadence, so the fast poll is paid for only while
    /// the pointer is actually near the overlay.
    /// </summary>
    private void PollCursor()
    {
        ApplyClickThrough();
        if (_disposed)
        {
            return;
        }

        try
        {
            _cursorPoll?.Change(_cursorIsNearWindow ? NearCursorPollInterval : FarCursorPollInterval, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
        }
    }

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
        IReadOnlyList<PixelRect> hoverRegions;
        lock (_gate)
        {
            regions = _cachedRegions;
            hoverRegions = _cachedHoverRegions;
        }

        if (!GetCursorPos(out var cursor) || !ScreenToClient(_windowHandle, ref cursor))
        {
            _cursorIsNearWindow = false;
            ReportCursorInsideHover(false);
            return false;
        }

        var clientRect = new NativeRect();
        _cursorIsNearWindow = GetClientRect(_windowHandle, ref clientRect)
            && cursor.X >= clientRect.Left - NearWindowMargin
            && cursor.X <= clientRect.Right + NearWindowMargin
            && cursor.Y >= clientRect.Top - NearWindowMargin
            && cursor.Y <= clientRect.Bottom + NearWindowMargin;

        var point = new PixelPoint(cursor.X, cursor.Y);
        ReportCursorInsideHover(hoverRegions.Any(region => region.Contains(point)));

        if (regions.Count == 0)
        {
            return false;
        }

        return regions.Any(region => region.Contains(point));
    }

    /// <summary>
    /// The overlay is click-through while the pointer is outside a control, so it never receives a
    /// pointer-entered message of its own. This poll is what tells it the pointer arrived.
    /// </summary>
    private void ReportCursorInsideHover(bool inside)
    {
        if (inside == _cursorInsideHover)
        {
            return;
        }

        _cursorInsideHover = inside;
        _cursorInsideChanged?.Invoke(inside);
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
