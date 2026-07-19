using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using DataService.App.ViewModels;

namespace DataService.App.Views;

public sealed partial class MainWindow : Window
{
    private IoErrorLogWindow? _ioErrorLogWindow;
    private LicenseWindow? _licenseWindow;
    private AuthenticationWindow? _authenticationWindow;

    public MainWindow()
    {
        InitializeComponent();
    }

    private void ViewLicenses_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_licenseWindow is { IsVisible: true })
        {
            _licenseWindow.Activate();
            return;
        }

        _licenseWindow = new LicenseWindow();
        _licenseWindow.Closed += (_, _) => _licenseWindow = null;
        _licenseWindow.Show(this);
    }

    private void ConfigureAuthentication_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_authenticationWindow is { IsVisible: true })
        {
            _authenticationWindow.Activate();
            return;
        }

        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        _authenticationWindow = new AuthenticationWindow
        {
            DataContext = viewModel.CreateAuthenticationViewModel()
        };
        _authenticationWindow.Closed += (_, _) => _authenticationWindow = null;
        _authenticationWindow.Show(this);
    }

    private void ViewIoErrorLog_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_ioErrorLogWindow is { IsVisible: true })
        {
            _ioErrorLogWindow.Activate();
            return;
        }

        _ioErrorLogWindow = new IoErrorLogWindow { DataContext = DataContext };
        _ioErrorLogWindow.Closed += (_, _) => _ioErrorLogWindow = null;
        _ioErrorLogWindow.Show(this);
    }

    private async void BrowseRootButton_OnClick(
        object? sender,
        RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select Root Folder",
            AllowMultiple = false
        });
        var selectedFolder = folders.FirstOrDefault();
        if (selectedFolder?.Path.IsFile == true)
        {
            viewModel.PendingRootPath = selectedFolder.Path.LocalPath;
        }
    }
}
