using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using UseNotch.App.ViewModels;
using UseNotch.Application;
using UseNotch.Domain;
using UseNotch.Platform.Windows.Overlay;

namespace UseNotch.App.Views;

public partial class OverlayWindow : Window
{
    /// <summary>
    /// How far in from the docked edge the pointer has to come before the notch opens. The strip is
    /// never drawn, so an idle desktop shows nothing at all.
    /// </summary>
    private const double EdgeTriggerWidth = 6;

    private readonly OverlayViewModel _viewModel;
    private readonly OverlayHoverDelays _delays;
    private readonly DispatcherTimer _hoverTimer;
    private OverlayTrigger? _pendingHoverTrigger;
    private bool _cursorInside;
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
    /// Reports what the overlay currently occupies, in its own device-independent coordinates.
    /// <para>
    /// Interactive regions are what accepts a click. Hover regions are only watched for the pointer, so
    /// the invisible edge strip can open the notch without ever capturing input.
    /// </para>
    /// </summary>
    public OverlayRegionSnapshot GetInteractiveRegions()
    {
        var interactive = new List<DipRect>();
        var hover = new List<DipRect>();

        if (NotchBar.IsVisible)
        {
            AddRegion(interactive, OpenAiCell);
            AddRegion(interactive, AnthropicCell);
            AddRegion(hover, NotchBar);
        }
        else
        {
            // Collapsed: nothing is drawn and nothing is clickable, but the edge strip still watches for
            // the pointer so the notch can open.
            hover.Add(new DipRect(ClientSize.Width - EdgeTriggerWidth, 0, EdgeTriggerWidth, ClientSize.Height));
        }

        if (DetailPanel.IsVisible)
        {
            AddRegion(interactive, DetailPanel);
            AddRegion(hover, DetailPanel);
        }

        return new OverlayRegionSnapshot(new DipSize(ClientSize.Width, ClientSize.Height), interactive, hover);
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

    /// <summary>
    /// Called by the platform layer when the pointer enters or leaves the overlay's hover regions. The
    /// window is click-through while the pointer is outside a control, so Avalonia's own pointer events
    /// cannot be relied on here.
    /// </summary>
    public void SetCursorInside(bool inside)
    {
        if (inside == _cursorInside)
        {
            return;
        }

        _cursorInside = inside;
        ScheduleHover(
            inside ? OverlayTrigger.PointerEntered : OverlayTrigger.PointerExited,
            inside ? _delays.Expand : _delays.Collapse);
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

    private void AddRegion(List<DipRect> regions, Control control)
    {
        if (control.Bounds.Width <= 0 || control.Bounds.Height <= 0)
        {
            return;
        }

        // A control that cannot be measured yet simply contributes no region, so a failure here can never
        // widen the interactive surface.
        if (control.TranslatePoint(default, this) is not { } origin)
        {
            return;
        }

        regions.Add(new DipRect(origin.X, origin.Y, control.Bounds.Width, control.Bounds.Height));
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
