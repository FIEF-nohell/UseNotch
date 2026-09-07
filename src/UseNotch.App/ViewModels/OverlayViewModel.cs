using CommunityToolkit.Mvvm.ComponentModel;

namespace UseNotch.App.ViewModels;

public partial class OverlayViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private string _detailTitle = "OpenAI status";

    [ObservableProperty]
    private string _detailText = "Static overlay validation. Provider data is not connected yet.";

    public void ShowDetail(string title, string detail)
    {
        DetailTitle = title;
        DetailText = detail;
        IsExpanded = true;
    }

    public void HideDetail() => IsExpanded = false;
}
