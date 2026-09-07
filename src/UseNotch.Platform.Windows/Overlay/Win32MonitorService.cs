using System.Runtime.InteropServices;

namespace UseNotch.Platform.Windows.Overlay;

public sealed class Win32MonitorService : IOverlayMonitorService
{
    public IReadOnlyList<DisplayMonitor> GetMonitors()
    {
        var monitors = new List<DisplayMonitor>();
        if (!EnumDisplayMonitors(nint.Zero, nint.Zero, (monitor, _, _, _) =>
            {
                var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
                if (GetMonitorInfo(monitor, ref info))
                {
                    var scaling = TryGetScaling(monitor);
                    monitors.Add(new DisplayMonitor(
                        info.DeviceName,
                        ToPixelRect(info.Monitor),
                        ToPixelRect(info.WorkArea),
                        scaling,
                        (info.Flags & MonitorInfoPrimary) != 0));
                }

                return true;
            }, nint.Zero))
        {
            throw new InvalidOperationException("Unable to enumerate Windows monitors.");
        }

        return monitors;
    }

    private static PixelRect ToPixelRect(NativeRect rect) =>
        new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);

    private static double TryGetScaling(nint monitor) =>
        GetDpiForMonitor(monitor, 0, out var horizontalDpi, out _) == 0
            ? horizontalDpi / 96d
            : 1;

    private const int MonitorInfoPrimary = 1;

    private delegate bool MonitorEnumProc(nint monitor, nint deviceContext, nint rectangle, nint data);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumDisplayMonitors(
        nint deviceContext,
        nint clipRectangle,
        MonitorEnumProc callback,
        nint data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo monitorInfo);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(nint monitor, int dpiType, out uint horizontalDpi, out uint verticalDpi);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect WorkArea;
        public int Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }
}
