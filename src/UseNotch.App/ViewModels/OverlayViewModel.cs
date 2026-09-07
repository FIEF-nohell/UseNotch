using CommunityToolkit.Mvvm.ComponentModel;
using UseNotch.Application;
using UseNotch.Domain;

namespace UseNotch.App.ViewModels;

public partial class OverlayViewModel : ObservableObject
{
    public static MockScenario? DevelopmentScenario { get; set; }

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private string _detailTitle = "OpenAI status";

    [ObservableProperty]
    private string _detailText = "Static overlay validation. Provider data is not connected yet.";

    [ObservableProperty]
    private string _openAiHeadline = "-";

    [ObservableProperty]
    private string _anthropicHeadline = "-";

    [ObservableProperty]
    private string _statusText = "Unavailable";

    [ObservableProperty]
    private string _activityText = "Activity unavailable";

    public void LoadDevelopmentScenario()
    {
        if (DevelopmentScenario is not { } scenario)
        {
            return;
        }
        var state = MockScenarioCatalog.Create(scenario);
        var headline = state.Snapshot?.Headline?.Limit.UsedFraction is { } fraction
            ? $"{fraction:P0} used"
            : "-";
        OpenAiHeadline = headline;
        StatusText = state.Status.Authentication switch
        {
            AuthenticationState.Authenticated => state.Status.Freshness == DataFreshness.Stale ? "Stale" : "Connected",
            AuthenticationState.Missing => "Sign in required",
            AuthenticationState.Expired => "Session expired",
            AuthenticationState.Rejected => "Access rejected",
            AuthenticationState.AccessDenied => "Access denied",
            AuthenticationState.Unsupported => "Unsupported",
            _ => scenario == MockScenario.Loading ? "Loading" : "Unavailable",
        };
        ActivityText = state.Activity is null
            ? "Activity unavailable"
            : state.Activity.Fidelity == ReadingFidelity.Derived ? $"{state.Activity.State}, estimated" : state.Activity.State.ToString();
        DetailText = $"{StatusText}. {ActivityText}.";
    }

    public void ShowDetail(string title, string detail)
    {
        DetailTitle = title;
        DetailText = detail;
        IsExpanded = true;
    }

    public void HideDetail() => IsExpanded = false;
}
