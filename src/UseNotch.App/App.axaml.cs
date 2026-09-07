using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using UseNotch.App.ViewModels;
using UseNotch.App.Views;

namespace UseNotch.App;

public partial class App : Avalonia.Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // M02 replaces this temporary window lifetime with the tray coordinator.
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
