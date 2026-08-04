using Avalonia.Controls;
using Avalonia.Interactivity;

namespace DataService.App.Views;

/// <summary>Modal yes/no prompt. Returns true from ShowDialog when confirmed.</summary>
public sealed partial class ConfirmationWindow : Window
{
    public ConfirmationWindow()
    {
        InitializeComponent();
    }

    public ConfirmationWindow(string title, string headline, string message, string confirmLabel)
        : this()
    {
        Title = title;
        HeadlineText.Text = headline;
        MessageText.Text = message;
        ConfirmButton.Content = confirmLabel;
    }

    private void Confirm_OnClick(object? sender, RoutedEventArgs e) => Close(true);

    private void Cancel_OnClick(object? sender, RoutedEventArgs e) => Close(false);
}
