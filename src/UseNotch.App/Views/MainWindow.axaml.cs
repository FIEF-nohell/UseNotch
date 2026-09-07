using Avalonia.Controls;
using UseNotch.App.Lifecycle;

namespace UseNotch.App.Views;

public partial class MainWindow : Window, ISettingsWindow
{
    private bool _allowClose;

    public MainWindow() => InitializeComponent();

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
    }

    public void ShowAndActivate()
    {
        Show();
        Activate();
    }

    public void CloseForShutdown()
    {
        _allowClose = true;
        Close();
    }
}
