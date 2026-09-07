using System.Runtime.InteropServices;

namespace UseNotch.Platform.Windows.Overlay;

public sealed class Win32OverlayWindowPlatform : IOverlayWindowPlatform
{
    private const int ExtendedStyleIndex = -20;
    private const int WindowProcedureIndex = -4;
    private const long ExtendedStyleNoActivate = 0x08000000L;
    private const long ExtendedStyleToolWindow = 0x00000080L;
    private const uint SetWindowPosNoSize = 0x0001;
    private const uint SetWindowPosNoMove = 0x0002;
    private const uint SetWindowPosNoActivate = 0x0010;
    private const uint SetWindowPosFrameChanged = 0x0020;
    private const int RegionOr = 2;
    private const int HitTestMessage = 0x0084;
    private const int MouseActivateMessage = 0x0021;
    private const int DpiChangedMessage = 0x02E0;
    private const int DisplayChangedMessage = 0x007E;
    private const int SettingChangedMessage = 0x001A;
    private const int HitTestTransparent = -1;
    private const int HitTestClient = 1;
    private const int MouseActivateNoActivate = 3;

    private readonly WindowProcedure _windowProcedure;
    private nint _windowHandle;
    private nint _originalWindowProcedure;
    private Func<IReadOnlyList<PixelRect>>? _interactiveRegionProvider;
    private Action? _nativeMetricsChanged;
    private bool _disposed;

    public Win32OverlayWindowPlatform() => _windowProcedure = WindowProcedureCallback;

    public nint GetForegroundWindow() => NativeGetForegroundWindow();

    public void Attach(
        nint windowHandle,
        Func<IReadOnlyList<PixelRect>> interactiveRegionProvider,
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
        SetWindowLongPtrChecked(windowHandle, ExtendedStyleIndex,
            new nint(extendedStyle | ExtendedStyleNoActivate | ExtendedStyleToolWindow));

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
    }

    public void UpdateInteractiveRegions()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_windowHandle == nint.Zero || _interactiveRegionProvider is null)
        {
            return;
        }

        var regions = _interactiveRegionProvider();
        nint combinedRegion = nint.Zero;
        try
        {
            foreach (var region in regions.Where(region => region.Width > 0 && region.Height > 0))
            {
                var nextRegion = CreateRectRgn(region.X, region.Y, region.Right, region.Bottom);
                if (nextRegion == nint.Zero)
                {
                    throw new InvalidOperationException("Unable to create an overlay input region.");
                }

                if (combinedRegion == nint.Zero)
                {
                    combinedRegion = nextRegion;
                }
                else
                {
                    _ = CombineRgn(combinedRegion, combinedRegion, nextRegion, RegionOr);
                    _ = DeleteObject(nextRegion);
                }
            }

            if (SetWindowRgn(_windowHandle, combinedRegion, true) == 0)
            {
                throw new InvalidOperationException("Unable to apply overlay input regions.");
            }

            combinedRegion = nint.Zero;
        }
        finally
        {
            if (combinedRegion != nint.Zero)
            {
                _ = DeleteObject(combinedRegion);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
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
        return _interactiveRegionProvider().Any(region => region.Contains(clientPoint));
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

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern nint CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int CombineRgn(nint destination, nint source1, nint source2, int mode);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowRgn(nint windowHandle, nint region, bool redraw);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteObject(nint objectHandle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ScreenToClient(nint windowHandle, ref NativePoint point);

    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")]
    private static extern nint NativeGetForegroundWindow();

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }
}
