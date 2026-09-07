using UseNotch.App.ViewModels;
using UseNotch.App.Views;

namespace UseNotch.App.Lifecycle;

public sealed class MainWindowSettingsFactory : ISettingsWindowFactory
{
    public ISettingsWindow Create() =>
        new MainWindow
        {
            DataContext = new MainWindowViewModel(),
        };
}
