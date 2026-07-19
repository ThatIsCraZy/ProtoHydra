using Avalonia;
using DataService.App.ViewModels;
using DataService.Core.Authentication;
using DataService.Core.Configuration;
using DataService.Core.Diagnostics;
using DataService.Core.Events;
using DataService.Infrastructure.Certificates;
using DataService.Infrastructure.Firewall;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using DataService.Protocols.Abstractions;
using DataService.Protocols.Ftp;
using DataService.Protocols.Http;
using DataService.Protocols.Ssh;
using DataService.Protocols.Tftp;

namespace DataService.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();

    public static IHost BuildHost()
        => Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton(AppConfigurationDefaults.Create());
                services.AddSingleton<AuthenticationSettingsStore>();
                services.AddSingleton(provider =>
                {
                    var store = provider.GetRequiredService<AuthenticationSettingsStore>();
                    return new RuntimeAuthenticationPolicy(
                        AuthenticationSettingsStore.CreatePolicy(store.Load()));
                });
                services.AddSingleton<IAuthenticationPolicy>(provider =>
                    provider.GetRequiredService<RuntimeAuthenticationPolicy>());
                services.AddSingleton<ITransferEventBus>(_ => new TransferEventBus());
                services.AddSingleton<IoErrorLog>();
                services.AddSingleton<ICertificateManager, CertificateManager>();
                services.AddSingleton<IFirewallStatusService, WindowsFirewallStatusService>();
                services.AddSingleton<IFirewallTemporaryRuleService, WindowsTemporaryFirewallRuleService>();
                services.AddSingleton<IProtocolAdapter>(serviceProvider => new HttpFileServerAdapter(
                    ProtocolKind.Http,
                    serviceProvider.GetRequiredService<ITransferEventBus>(),
                    serviceProvider.GetRequiredService<IAuthenticationPolicy>()));
                services.AddSingleton<IProtocolAdapter>(serviceProvider =>
                {
                    var configuration = serviceProvider.GetRequiredService<AppConfiguration>();
                    return new HttpFileServerAdapter(
                        ProtocolKind.Https,
                        serviceProvider.GetRequiredService<ITransferEventBus>(),
                        serviceProvider.GetRequiredService<IAuthenticationPolicy>(),
                        serviceProvider.GetRequiredService<ICertificateManager>(),
                        new CertificateSettings(configuration.CertificateSettings.DirectoryPath));
                });
                services.AddSingleton<IProtocolAdapter>(serviceProvider => new FtpFileServerAdapter(
                    ProtocolKind.Ftp,
                    serviceProvider.GetRequiredService<ITransferEventBus>(),
                    serviceProvider.GetRequiredService<IAuthenticationPolicy>()));
                services.AddSingleton<IProtocolAdapter>(serviceProvider =>
                {
                    var configuration = serviceProvider.GetRequiredService<AppConfiguration>();
                    return new FtpFileServerAdapter(
                        ProtocolKind.Ftps,
                        serviceProvider.GetRequiredService<ITransferEventBus>(),
                        serviceProvider.GetRequiredService<IAuthenticationPolicy>(),
                        serviceProvider.GetRequiredService<ICertificateManager>(),
                        new CertificateSettings(configuration.CertificateSettings.DirectoryPath));
                });
                services.AddSingleton<IProtocolAdapter>(serviceProvider => new TftpFileServerAdapter(
                    serviceProvider.GetRequiredService<ITransferEventBus>()));
                services.AddSingleton(serviceProvider =>
                {
                    var configuration = serviceProvider.GetRequiredService<AppConfiguration>();
                    return new SharedSshServer(
                        serviceProvider.GetRequiredService<ITransferEventBus>(),
                        configuration.SshHostKeySettings.DirectoryPath,
                        serviceProvider.GetRequiredService<IAuthenticationPolicy>());
                });
                services.AddSingleton<IProtocolAdapter>(serviceProvider =>
                    new SftpFileServerAdapter(serviceProvider.GetRequiredService<SharedSshServer>()));
                services.AddSingleton<IProtocolAdapter>(serviceProvider =>
                    new ScpFileServerAdapter(serviceProvider.GetRequiredService<SharedSshServer>()));
                services.AddSingleton<MainViewModel>();
            })
            .Build();
}
