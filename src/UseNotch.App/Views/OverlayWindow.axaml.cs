using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using UseNotch.App.ViewModels;
using UseNotch.Platform.Windows.Overlay;
using OverlayPixelRect = UseNotch.Platform.Windows.Overlay.PixelRect;

namespace UseNotch.App.Views;

public partial class OverlayWindow : Window
{
    private readonly OverlayViewModel _viewModel;
    private bool _allowClose;

    public OverlayWindow()
        : this(new OverlayViewModel())
    {
    }

    public OverlayWindow(OverlayViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = _viewModel;
        _viewModel.LoadDevelopmentScenario();
        ScalingChanged += (_, _) => InteractiveRegionsChanged?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? InteractiveRegionsChanged;

    public IReadOnlyList<OverlayPixelRect> GetInteractivePixelRegions()
    {
        var interactiveControls = new List<Control>
        {
            OpenAiCell,
            AnthropicCell,
        };
        if (DetailPanel.IsVisible)
        {
            interactiveControls.Add(DetailPanel);
        }

        return interactiveControls.Select(ToPixelRect).ToArray();
    }

    public void CloseForShutdown()
    {
        _allowClose = true;
        Close();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
        }

        base.OnClosing(e);
    }

    private OverlayPixelRect ToPixelRect(Control control)
    {
        var point = control.TranslatePoint(default, this)
            ?? throw new InvalidOperationException("The overlay control is not attached to its window.");
        var scale = RenderScaling;
        var x = (int)Math.Floor(point.X * scale);
        var y = (int)Math.Floor(point.Y * scale);
        var right = (int)Math.Ceiling((point.X + control.Bounds.Width) * scale);
        var bottom = (int)Math.Ceiling((point.Y + control.Bounds.Height) * scale);
        return new OverlayPixelRect(x, y, right - x, bottom - y);
    }

    private void OnOpenAiClicked(object? sender, RoutedEventArgs e)
    {
        _viewModel.ShowDetail("OpenAI / Codex status", $"{_viewModel.OpenAiHeadline}. {_viewModel.OpenAiStatusText}.");
        ShowDetail();
    }

    private void OnAnthropicClicked(object? sender, RoutedEventArgs e)
    {
        _viewModel.ShowDetail("Anthropic / Claude Code status", $"{_viewModel.AnthropicHeadline}. {_viewModel.AnthropicStatusText}.");
        ShowDetail();
    }

    private void OnCloseDetailClicked(object? sender, RoutedEventArgs e)
    {
        _viewModel.HideDetail();
        DetailPanel.IsVisible = false;
        Title = "UseNotch overlay";
        InteractiveRegionsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ShowDetail()
    {
        DetailTitle.Text = _viewModel.DetailTitle;
        DetailText.Text = _viewModel.DetailText;
        DetailPanel.IsVisible = true;
        Title = "UseNotch overlay expanded";
        InteractiveRegionsChanged?.Invoke(this, EventArgs.Empty);
    }
}
