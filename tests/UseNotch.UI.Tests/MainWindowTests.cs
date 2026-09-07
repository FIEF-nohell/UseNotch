using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using UseNotch.App.ViewModels;
using UseNotch.App.Views;

namespace UseNotch.UI.Tests;

public class MainWindowTests
{
    [AvaloniaFact]
    public void Window_loads_compiled_XAML_and_updates_from_generated_MVVM_property()
    {
        var viewModel = new MainWindowViewModel();
        var window = new MainWindow { DataContext = viewModel };

        try
        {
            window.Show();
            Assert.Equal(viewModel.Title, window.Title);
            var status = window.FindControl<TextBlock>("StatusText");
            Assert.NotNull(status);
            Assert.Equal(viewModel.StatusMessage, status.Text);

            viewModel.StatusMessage = "A fixture update reached the view.";
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(viewModel.StatusMessage, status.Text);
        }
        finally
        {
            window.CloseForShutdown();
        }
    }

    [AvaloniaFact]
    public void Ordinary_close_hides_the_settings_window_until_app_shutdown()
    {
        var window = new MainWindow { DataContext = new MainWindowViewModel() };

        window.Show();
        window.Close();
        Dispatcher.UIThread.RunJobs();

        Assert.False(window.IsVisible);

        window.CloseForShutdown();
    }
}
