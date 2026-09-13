using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using DataService.App.ViewModels;

namespace DataService.App.Views;

public sealed partial class SerialConsoleView : UserControl
{
    public SerialConsoleView()
    {
        InitializeComponent();
        Terminal.TextSubmitted += OnTerminalTextSubmitted;
        DataContextChanged += (_, _) =>
        {
            if (DataContext is SerialConsoleViewModel viewModel)
            {
                viewModel.CopyToClipboardAsync = CopyToClipboardAsync;
            }
        };
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // Switching to this tab should put the caret in the console, not on the first combo box.
        Dispatcher.UIThread.Post(() => Terminal.Focus(), DispatcherPriority.Input);
    }

    private void OnTerminalTextSubmitted(object? sender, string text)
    {
        if (DataContext is SerialConsoleViewModel viewModel)
        {
            viewModel.SendText(text);
        }
    }

    private async Task CopyToClipboardAsync(string text)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
        {
            await clipboard.SetTextAsync(text);
        }
    }
}
