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
    private string _openAiStatusText = "Unavailable";

    [ObservableProperty]
    private string _anthropicStatusText = "Unavailable";

    [ObservableProperty]
    private string _activityText = "Activity unavailable";

    public void LoadDevelopmentScenario()
    {
        if (DevelopmentScenario is not { } scenario)
        {
            return;
        }
        ApplyScenario(ProviderId.OpenAi, scenario);
        ApplyScenario(ProviderId.Anthropic, scenario);
    }

    public void ApplyScenario(ProviderId provider, MockScenario scenario)
    {
        var state = MockScenarioCatalog.Create(provider, scenario);
        var headline = state.Snapshot?.Headline?.Limit.UsedFraction is { } fraction
            ? $"{fraction * 100:0}% used"
            : state.Snapshot?.Headline is not null ? "-"
            : state.Status.Error is not null ? "Usage unavailable" : "Awaiting updated window";
        var status = state.Status.Authentication switch
        {
            AuthenticationState.Authenticated => state.Status.Freshness == DataFreshness.Stale ? "Stale" : "Connected",
            AuthenticationState.Missing => "Sign in required",
            AuthenticationState.Expired => "Session expired",
            AuthenticationState.Rejected => "Access rejected",
            AuthenticationState.AccessDenied => "Access denied",
            AuthenticationState.Unsupported => "Unsupported",
            _ => scenario == MockScenario.Loading ? "Loading" : "Unavailable",
        };
        var activity = state.Activity is null
            ? "Activity unavailable"
            : state.Activity.Fidelity == ReadingFidelity.Derived ? $"{state.Activity.State}, estimated" : state.Activity.State.ToString();
        if (provider == ProviderId.OpenAi)
        {
            OpenAiHeadline = headline;
            OpenAiStatusText = status;
            StatusText = status;
            ActivityText = activity;
            DetailText = $"{StatusText}. {ActivityText}.";
        }
        else
        {
            AnthropicHeadline = headline;
            AnthropicStatusText = status;
        }
    }

    public void ApplyRuntimeState(ProviderRuntimeState state)
    {
        var headline = state.Snapshot?.Headline?.Limit.UsedFraction is { } fraction
            ? $"{fraction * 100:0}% used"
            : state.Snapshot?.Headline is not null ? "-"
            : state.Status.Error is not null ? "Usage unavailable" : "Awaiting updated window";
        var status = state.Status.Authentication switch
        {
            AuthenticationState.Authenticated => state.Status.Freshness switch
            {
                DataFreshness.Fresh => "Connected",
                DataFreshness.Stale => "Stale",
                DataFreshness.Expired => "Expired reading",
                _ => "Reading unavailable",
            },
            AuthenticationState.Missing => "Sign in required",
            AuthenticationState.Expired => "Session expired",
            AuthenticationState.Rejected => "Access rejected",
            AuthenticationState.AccessDenied => "Access denied",
            AuthenticationState.Unsupported => "Unsupported",
            _ => state.Status.Error?.SafeMessage ?? "Loading",
        };
        if (state.Connection.Provider == ProviderId.OpenAi)
        {
            OpenAiHeadline = headline;
            OpenAiStatusText = status;
            StatusText = status;
            DetailText = $"OpenAI / Codex quota. {headline}. {status}.";
        }
        else
        {
            AnthropicHeadline = headline;
            AnthropicStatusText = status;
        }
    }

    public void ShowDetail(string title, string detail)
    {
        DetailTitle = title;
        DetailText = detail;
        IsExpanded = true;
    }

    public void HideDetail() => IsExpanded = false;
}
