using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using LenxTool.App.ViewModels;

namespace LenxTool.App;

public partial class MainWindow : Window
{
    private readonly ShellViewModel _viewModel;

    public MainWindow(ShellViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.K && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            _viewModel.IsCommandPaletteOpen = true;
            Dispatcher.BeginInvoke(CommandSearchBox.Focus, DispatcherPriority.Input);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _viewModel.IsCommandPaletteOpen)
        {
            _viewModel.IsCommandPaletteOpen = false;
            e.Handled = true;
        }
    }
}
