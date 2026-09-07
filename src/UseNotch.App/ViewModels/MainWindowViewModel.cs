using CommunityToolkit.Mvvm.ComponentModel;

namespace UseNotch.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    public string Title => "UseNotch";

    [ObservableProperty]
    private string _statusMessage = "Provider connections and usage polling are scheduled next.";
}
