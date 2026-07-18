using Avalonia.Controls;
using Avalonia.Interactivity;

namespace DataService.App.Views;

public sealed partial class IoErrorLogWindow : Window
{
    public IoErrorLogWindow()
    {
        InitializeComponent();
    }

    private void Close_OnClick(object? sender, RoutedEventArgs e)
        => Close();
}
