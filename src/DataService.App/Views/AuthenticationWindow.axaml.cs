using Avalonia.Controls;
using Avalonia.Interactivity;

namespace DataService.App.Views;

public sealed partial class AuthenticationWindow : Window
{
    public AuthenticationWindow()
    {
        InitializeComponent();
    }

    private void Close_OnClick(object? sender, RoutedEventArgs e) => Close();
}
