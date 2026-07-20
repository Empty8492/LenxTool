using LenxTool.App.Mvvm;

namespace LenxTool.App.ViewModels;

public sealed class TrendSourceFilter(
    string platform,
    int count,
    bool isSelected = true) : ObservableObject
{
    private bool _isSelected = isSelected;

    public string Platform { get; } = platform;
    public int Count { get; } = count;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}
