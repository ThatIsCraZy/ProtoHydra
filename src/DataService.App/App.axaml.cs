using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using DataService.App.ViewModels;
using DataService.App.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DataService.App;

public sealed partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override async void OnFrameworkInitializationCompleted()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        var host = Program.BuildHost();
        await host.StartAsync();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = host.Services.GetRequiredService<MainViewModel>()
            };
            desktop.Exit += async (_, _) =>
            {
                await host.Services.GetRequiredService<MainViewModel>().ShutdownAsync(CancellationToken.None);
                await host.StopAsync(TimeSpan.FromSeconds(5));
                host.Dispose();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
