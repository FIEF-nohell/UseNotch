using Microsoft.Win32;

namespace UseNotch.Platform.Windows.Session;

/// <summary>
/// Reports whether the interactive session is locked, so background work can be suspended while nobody
/// can see the overlay. It only observes session state; it never blocks or changes it.
/// </summary>
public sealed class SessionLockWatcher : IDisposable
{
    private readonly Action<bool> _onLockChanged;
    private bool _disposed;

    public SessionLockWatcher(Action<bool> onLockChanged)
    {
        _onLockChanged = onLockChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        switch (e.Reason)
        {
            case SessionSwitchReason.SessionLock:
            case SessionSwitchReason.SessionLogoff:
            case SessionSwitchReason.ConsoleDisconnect:
            case SessionSwitchReason.RemoteDisconnect:
                _onLockChanged(true);
                break;
            case SessionSwitchReason.SessionUnlock:
            case SessionSwitchReason.SessionLogon:
            case SessionSwitchReason.ConsoleConnect:
            case SessionSwitchReason.RemoteConnect:
                _onLockChanged(false);
                break;
            default:
                break;
        }
    }
}
