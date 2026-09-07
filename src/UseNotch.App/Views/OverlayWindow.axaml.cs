using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using UseNotch.App.ViewModels;
using UseNotch.Application;
using UseNotch.Domain;
using UseNotch.Platform.Windows.Overlay;

namespace UseNotch.App.Views;

public partial class OverlayWindow : Window
{
    private readonly OverlayViewModel _viewModel;
    private readonly OverlayHoverDelays _delays;
    private readonly DispatcherTimer _hoverTimer;
    private OverlayTrigger? _pendingHoverTrigger;
    private bool _allowClose;

    public OverlayWindow()
        : this(new OverlayViewModel())
    {
    }

    public OverlayWindow(OverlayViewModel viewModel, OverlayHoverDelays? delays = null)
    {
        _viewModel = viewModel;
        _delays = delays ?? OverlayHoverDelays.Default;
        InitializeComponent();
        DataContext = _viewModel;
        _viewModel.LoadDevelopmentScenario();
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _hoverTimer = new DispatcherTimer { Interval = _delays.Expand };
        _hoverTimer.Tick += OnHoverTimerTick;
        ScalingChanged += (_, _) => InteractiveRegionsChanged?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? InteractiveRegionsChanged;

    /// <summary>
    /// Only what is actually visible becomes interactive. There is no invisible hover-capture padding, so
    /// input outside the drawn surface always reaches the window underneath. Regions are reported in this
    /// window's own device-independent coordinates; the platform layer converts them to pixels.
    /// </summary>
    public OverlayRegionSnapshot GetInteractiveRegions()
    {
        var interactiveControls = new List<Control>();
        if (CollapsedHandle.IsVisible)
        {
            interactiveControls.Add(CollapsedHandle);
        }

        if (ProviderCells.IsVisible)
        {
            interactiveControls.Add(OpenAiCell);
            interactiveControls.Add(AnthropicCell);
        }

        if (DetailPanel.IsVisible)
        {
            interactiveControls.Add(DetailPanel);
        }

        var regions = new List<DipRect>();
        foreach (var control in interactiveControls)
        {
            if (control.Bounds.Width <= 0 || control.Bounds.Height <= 0)
            {
                continue;
            }

            // A control that cannot be measured yet simply contributes no region, so a failure here can
            // never widen the interactive surface.
            if (control.TranslatePoint(default, this) is not { } origin)
            {
                continue;
            }

            regions.Add(new DipRect(origin.X, origin.Y, control.Bounds.Width, control.Bounds.Height));
        }

        return new OverlayRegionSnapshot(new DipSize(ClientSize.Width, ClientSize.Height), regions);
    }

    public void CloseForShutdown()
    {
        _allowClose = true;
        _hoverTimer.Stop();
        Close();
    }

    public void SetPresentation(OverlayTrigger trigger)
    {
        _viewModel.Apply(trigger);
        RefreshRegions();
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

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        ScheduleHover(OverlayTrigger.PointerEntered, _delays.Expand);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        ScheduleHover(OverlayTrigger.PointerExited, _delays.Collapse);
    }

    /// <summary>
    /// Hover changes are delayed on both sides. A pointer crossing the screen edge cannot flash the
    /// overlay open, and a pointer travelling between the two cells cannot make it collapse under itself.
    /// </summary>
    private void ScheduleHover(OverlayTrigger trigger, TimeSpan delay)
    {
        _hoverTimer.Stop();
        _pendingHoverTrigger = trigger;
        _hoverTimer.Interval = delay;
        _hoverTimer.Start();
    }

    private void OnHoverTimerTick(object? sender, EventArgs e)
    {
        _hoverTimer.Stop();
        if (_pendingHoverTrigger is { } trigger)
        {
            _pendingHoverTrigger = null;
            SetPresentation(trigger);
        }
    }

    private void OnOpenAiClicked(object? sender, RoutedEventArgs e) => OpenDetail(ProviderId.OpenAi);

    private void OnAnthropicClicked(object? sender, RoutedEventArgs e) => OpenDetail(ProviderId.Anthropic);

    private void OnCloseDetailClicked(object? sender, RoutedEventArgs e)
    {
        _viewModel.HideDetail();
        RefreshRegions();
    }

    private void OpenDetail(ProviderId provider)
    {
        _viewModel.ShowDetail(provider);
        RefreshRegions();
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(OverlayViewModel.ShowsProviderCells) or nameof(OverlayViewModel.ShowsDetails))
        {
            RefreshRegions();
        }
    }

    private void RefreshRegions()
    {
        // Titles stay meaningful for the native smoke checks, which identify the window by its title.
        Title = _viewModel.ShowsDetails
            ? "UseNotch overlay expanded"
            : _viewModel.ShowsProviderCells
                ? "UseNotch overlay open"
                : "UseNotch overlay";
        UpdateLayout();
        InteractiveRegionsChanged?.Invoke(this, EventArgs.Empty);
    }
}
