namespace UseNotch.App.Lifecycle;

public interface ISettingsWindow
{
    void ShowAndActivate();

    void Hide();

    void CloseForShutdown();
}

public interface ISettingsWindowFactory
{
    ISettingsWindow Create();
}

public sealed class AppLifecycleCoordinator : IDisposable
{
    private readonly ISettingsWindowFactory _windowFactory;
    private readonly Action _shutdown;
    private ISettingsWindow? _settingsWindow;
    private bool _shutdownRequested;
    private bool _disposed;

    public AppLifecycleCoordinator(ISettingsWindowFactory windowFactory, Action shutdown)
    {
        _windowFactory = windowFactory;
        _shutdown = shutdown;
    }

    public bool IsShutdownRequested => _shutdownRequested;

    public void ShowSettings()
    {
        ThrowIfDisposed();
        if (_shutdownRequested)
        {
            return;
        }

        _settingsWindow ??= _windowFactory.Create();
        _settingsWindow.ShowAndActivate();
    }

    public void HideSettings()
    {
        if (_shutdownRequested || _disposed)
        {
            return;
        }

        _settingsWindow?.Hide();
    }

    public void Quit()
    {
        if (_shutdownRequested || _disposed)
        {
            return;
        }

        _shutdownRequested = true;
        _settingsWindow?.CloseForShutdown();
        _shutdown();
    }

    public void Dispose()
    {
        _disposed = true;
        _settingsWindow = null;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
