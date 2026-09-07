using UseNotch.App.Lifecycle;

namespace UseNotch.UI.Tests;

public class AppLifecycleCoordinatorTests
{
    [Fact]
    public void Settings_window_is_created_lazily_reused_and_hidden()
    {
        var factory = new FakeSettingsWindowFactory();
        var shutdownCount = 0;
        using var coordinator = new AppLifecycleCoordinator(factory, () => shutdownCount++);

        coordinator.ShowSettings();
        coordinator.ShowSettings();
        coordinator.HideSettings();

        var window = Assert.Single(factory.CreatedWindows);
        Assert.Equal(2, window.ShowAndActivateCount);
        Assert.Equal(1, window.HideCount);
        Assert.Equal(0, shutdownCount);
        Assert.False(coordinator.IsShutdownRequested);
    }

    [Fact]
    public void Quit_is_idempotent_and_closes_the_created_window_before_shutdown()
    {
        var factory = new FakeSettingsWindowFactory();
        var sequence = new List<string>();
        using var coordinator = new AppLifecycleCoordinator(factory, () => sequence.Add("shutdown"));
        coordinator.ShowSettings();
        factory.CreatedWindows.Single().Closed = () => sequence.Add("window-close");

        coordinator.Quit();
        coordinator.Quit();

        var window = Assert.Single(factory.CreatedWindows);
        Assert.Equal(["window-close", "shutdown"], sequence);
        Assert.True(coordinator.IsShutdownRequested);
        Assert.Equal(1, window.CloseForShutdownCount);
    }

    [Fact]
    public void Disposed_coordinator_rejects_new_windows()
    {
        var factory = new FakeSettingsWindowFactory();
        var coordinator = new AppLifecycleCoordinator(factory, () => { });
        coordinator.Dispose();

        Assert.Throws<ObjectDisposedException>(coordinator.ShowSettings);
        Assert.Empty(factory.CreatedWindows);
    }

    private sealed class FakeSettingsWindowFactory : ISettingsWindowFactory
    {
        public List<FakeSettingsWindow> CreatedWindows { get; } = [];

        public ISettingsWindow Create()
        {
            var window = new FakeSettingsWindow();
            CreatedWindows.Add(window);
            return window;
        }
    }

    private sealed class FakeSettingsWindow : ISettingsWindow
    {
        public int ShowAndActivateCount { get; private set; }

        public int HideCount { get; private set; }

        public int CloseForShutdownCount { get; private set; }

        public Action? Closed { get; set; }

        public void ShowAndActivate() => ShowAndActivateCount++;

        public void Hide() => HideCount++;

        public void CloseForShutdown()
        {
            CloseForShutdownCount++;
            Closed?.Invoke();
        }
    }
}
