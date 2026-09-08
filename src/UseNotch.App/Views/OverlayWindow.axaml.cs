using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
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
    /// never drawn and never accepts a click, so an idle desktop shows nothing at all and nothing under
    /// the strip loses input. It is only watched for the pointer.
    /// <para>
    /// This was six device-independent pixels, which is about ten physical pixels at 175% scale. That
    /// only worked if the pointer was thrown hard enough to stop against the screen edge; approaching
    /// deliberately and stopping just short of the edge missed the strip entirely. Widening it costs
    /// nothing, because widening a hover region cannot capture input.
    /// </para>
    /// </summary>
    private const double EdgeTriggerWidth = 28;

    /// <summary>How far off the docked edge the notch starts before it slides into place.</summary>
    private const double NotchEntranceOffset = 120;

    private readonly OverlayViewModel _viewModel;
    private readonly OverlayHoverDelays _delays;
    private readonly DispatcherTimer _hoverTimer;
    private OverlayTrigger? _pendingHoverTrigger;
    /// <summary>
    /// The notch's slide transform. A name on a transform does not produce a generated field, because
    /// the transform is not a control in the visual tree, so it is resolved once here instead.
    /// </summary>
    private readonly TranslateTransform? _notchSlide;

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
        _notchSlide = NotchBar.RenderTransform as TranslateTransform;
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

        // The edge strip is watched in every state, not only while collapsed. Whatever opens the notch
        // has to keep it open, or the overlay collapses under a pointer that never moved.
        //
        // The strip runs the full height of the window while the notch itself is far shorter, so a
        // pointer resting on the strip above or below the notch used to be inside the watched region
        // when collapsed and outside it the instant the notch appeared. That collapsed it, which
        // restored the strip, which opened it again: a flicker loop with the pointer held still.
        var hover = new List<DipRect>
        {
            new(ClientSize.Width - EdgeTriggerWidth, 0, EdgeTriggerWidth, ClientSize.Height),
        };

        if (NotchBar.IsVisible)
        {
            // Everything under the notch is reported at its resting position, with the slide taken back
            // out. TranslatePoint walks the render transform too, so mid-animation these would be
            // reported up to the entrance offset away from where they will settle, which pushed them off
            // the window entirely. Nothing recomputes regions when a transition ends, so the cells would
            // have stayed unclickable after the notch finished arriving.
            var slide = _notchSlide?.X ?? 0;
            AddRegion(interactive, OpenAiCell, slide);
            AddRegion(interactive, AnthropicCell, slide);
            AddRegion(hover, NotchBar, slide);
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

    private void AddRegion(List<DipRect> regions, Control control, double horizontalOffset = 0)
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

        regions.Add(new DipRect(origin.X - horizontalOffset, origin.Y, control.Bounds.Width, control.Bounds.Height));
    }

    /// <summary>
    /// Points the callout's tail at the cell whose details are open. Centring it looked correct only
    /// when the opened provider happened to be the middle one, which with two cells is never.
    /// </summary>
    private void AlignDetailTail()
    {
        if (!DetailPanel.IsVisible || _viewModel.DetailProvider is not { } detail)
        {
            return;
        }

        // The ring, not the whole cell: a cell also contains the percentage label underneath, so its
        // midpoint sits visibly below the ring the tail is supposed to be pointing at.
        var ring = detail.Provider == ProviderId.OpenAi ? (Control)OpenAiRing : AnthropicRing;
        if (ring.Bounds.Height <= 0 || DetailTail.Height <= 0)
        {
            return;
        }

        // A ring that cannot be translated yet simply leaves the tail where it is, rather than moving it
        // somewhere arbitrary.
        if (ring.TranslatePoint(new Point(0, ring.Bounds.Height / 2), DetailPanel) is not { } centre)
        {
            return;
        }

        var top = centre.Y - (DetailTail.Height / 2);
        DetailTail.Margin = new Thickness(0, Math.Max(0, top), 0, 0);
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

        if (e.PropertyName is nameof(OverlayViewModel.ShowsProviderCells))
        {
            AnimateNotchEntrance();
        }
    }

    /// <summary>
    /// Slides the notch in from behind the screen edge as it appears.
    /// <para>
    /// The resting values have to be applied in a later dispatcher pass. Setting them in the same pass
    /// that made the notch visible would leave the transition nothing to animate from, and the notch
    /// would simply pop into place.
    /// </para>
    /// </summary>
    private void AnimateNotchEntrance()
    {
        if (_notchSlide is null)
        {
            NotchBar.Opacity = _viewModel.ShowsProviderCells ? 1 : 0;
            return;
        }

        if (!_viewModel.ShowsProviderCells)
        {
            // Hidden again: park it off the edge so the next entrance starts from outside the screen.
            NotchBar.Opacity = 0;
            _notchSlide.X = NotchEntranceOffset;
            return;
        }

        if (_viewModel.ReducedMotion)
        {
            NotchBar.Opacity = 1;
            _notchSlide.X = 0;
            return;
        }

        NotchBar.Opacity = 0;
        _notchSlide.X = NotchEntranceOffset;
        Dispatcher.UIThread.Post(
            () =>
            {
                if (_viewModel.ShowsProviderCells)
                {
                    NotchBar.Opacity = 1;
                    _notchSlide.X = 0;
                }
            },
            DispatcherPriority.Background);
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
        AlignDetailTail();
        InteractiveRegionsChanged?.Invoke(this, EventArgs.Empty);
    }
}
