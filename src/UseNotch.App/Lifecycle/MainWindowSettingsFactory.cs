using UseNotch.App.ViewModels;
using UseNotch.App.Views;

namespace UseNotch.App.Lifecycle;

public sealed class MainWindowSettingsFactory(Func<SettingsViewModel> viewModelFactory) : ISettingsWindowFactory
{
    public ISettingsWindow Create()
    {
        var viewModel = viewModelFactory();
        var window = new MainWindow { DataContext = viewModel };
        _ = viewModel.LoadAsync();
        return window;
    }
}
