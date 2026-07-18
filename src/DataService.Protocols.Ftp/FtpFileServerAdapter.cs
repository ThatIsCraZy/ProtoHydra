using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using DataService.Core.Events;
using DataService.Infrastructure.Certificates;
using DataService.Protocols.Abstractions;
using FubarDev.FtpServer;
using FubarDev.FtpServer.AccountManagement;
using FubarDev.FtpServer.AccountManagement.Directories.SingleRootWithoutHome;
using FubarDev.FtpServer.FileSystem;
using FubarDev.FtpServer.FileSystem.DotNet;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DataService.Protocols.Ftp;

public sealed class FtpFileServerAdapter : IProtocolAdapter
{
    private readonly ITransferEventBus _eventBus;
    private readonly ICertificateManager? _certificateManager;
    private readonly CertificateSettings? _certificateSettings;
    private readonly bool _useTls;
    private IHost? _host;
    private IFtpServerHost? _ftpServerHost;

    public FtpFileServerAdapter(ITransferEventBus eventBus)
        : this(ProtocolKind.Ftp, eventBus)
    {
    }

    public FtpFileServerAdapter(
        ProtocolKind protocol,
        ITransferEventBus eventBus,
        ICertificateManager? certificateManager = null,
        CertificateSettings? certificateSettings = null)
    {
        if (protocol is not (ProtocolKind.Ftp or ProtocolKind.Ftps))
        {
            throw new ArgumentException("Only FTP and FTPS are supported.", nameof(protocol));
        }

        Protocol = protocol;
        _eventBus = eventBus;
        _certificateManager = certificateManager;
        _certificateSettings = certificateSettings;
        _useTls = protocol == ProtocolKind.Ftps;
        Capabilities = new ProtocolCapabilities(
            SupportsDownload: true,
            SupportsUpload: true,
            SupportsListing: true,
            SupportsAuthentication: true,
            UsesEncryption: _useTls);
    }

    public ProtocolKind Protocol { get; }

    public ProtocolCapabilities Capabilities { get; }

    public ProtocolRuntimeState State { get; private set; } = ProtocolRuntimeState.Stopped;

    public string? UnavailableReason => null;

    public Task<ProtocolValidationResult> ValidateAsync(
        ProtocolConfiguration configuration,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!Directory.Exists(configuration.RootPath))
        {
            return Task.FromResult(ProtocolValidationResult.Failure("Root folder does not exist."));
        }

        if (!IPAddress.TryParse(configuration.BindAddress, out var address))
        {
            return Task.FromResult(ProtocolValidationResult.Failure("Bind address is invalid."));
        }

        if (configuration.Port is < IPEndPoint.MinPort or > IPEndPoint.MaxPort)
        {
            return Task.FromResult(ProtocolValidationResult.Failure("Port is outside the valid TCP port range."));
        }

        try
        {
            using var listener = new TcpListener(address, configuration.Port);
            listener.Start();
            listener.Stop();
        }
        catch (SocketException ex)
        {
            return Task.FromResult(ProtocolValidationResult.Failure($"Port is not available: {ex.Message}"));
        }

        return Task.FromResult(ProtocolValidationResult.Success);
    }

    public async Task StartAsync(
        ProtocolConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (State is ProtocolRuntimeState.Running or ProtocolRuntimeState.Starting)
        {
            return;
        }

        var validation = await ValidateAsync(configuration, cancellationToken);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(validation.Message);
        }

        State = ProtocolRuntimeState.Starting;
        Publish(TransferEventKind.ListenerStarting, configuration, TransferResult.Success);

        try
        {
            var rootPath = Path.GetFullPath(configuration.RootPath);
            var certificate = _useTls
                ? await GetCertificateAsync(cancellationToken)
                : null;

            _host = Host.CreateDefaultBuilder()
                .ConfigureLogging(logging => logging.ClearProviders())
                .ConfigureServices(services =>
                {
                    services.AddSingleton(new FtpInstrumentationContext(_eventBus, Protocol, rootPath));
                    services.AddSingleton<AcceptAnyFtpMembershipProvider>();
                    services.AddSingleton<IMembershipProvider>(provider =>
                        provider.GetRequiredService<AcceptAnyFtpMembershipProvider>());
                    services.AddSingleton<IMembershipProviderAsync>(provider =>
                        provider.GetRequiredService<AcceptAnyFtpMembershipProvider>());
                    services.Configure<FtpServerOptions>(options =>
                    {
                        options.ServerAddress = configuration.BindAddress;
                        options.Port = configuration.Port;
                    });
                    services.Configure<DotNetFileSystemOptions>(options =>
                    {
                        options.RootPath = rootPath;
                        options.AllowNonEmptyDirectoryDelete = false;
                        options.FlushAfterWrite = true;
                    });
                    if (certificate is not null)
                    {
                        services.Configure<AuthTlsOptions>(options =>
                        {
                            options.ServerCertificate = certificate;
                            options.ImplicitFtps = configuration.Port == 990;
                        });
                    }

                    services.AddFtpServer(builder => builder
                        .UseDotNetFileSystem()
                        .UseSingleRoot(options => options.RootPath = "/"));
                    services.AddSingleton<IFileSystemClassFactory, InstrumentedFtpFileSystemFactory>();
                })
                .Build();

            await _host.StartAsync(cancellationToken);
            _ftpServerHost = _host.Services.GetRequiredService<IFtpServerHost>();
            await _ftpServerHost.StartAsync(cancellationToken);
            await WaitUntilListeningAsync(configuration, cancellationToken);

            State = ProtocolRuntimeState.Running;
            Publish(TransferEventKind.ListenerStarted, configuration, TransferResult.Success);
        }
        catch
        {
            State = ProtocolRuntimeState.Faulted;
            Publish(TransferEventKind.ListenerFaulted, configuration, TransferResult.Failed);
            await DisposeHostAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (State is ProtocolRuntimeState.Stopped or ProtocolRuntimeState.Stopping)
        {
            return;
        }

        State = ProtocolRuntimeState.Stopping;
        if (_ftpServerHost is not null)
        {
            await _ftpServerHost.StopAsync(cancellationToken);
        }

        await DisposeHostAsync(cancellationToken);
        State = ProtocolRuntimeState.Stopped;
    }

    private static async Task WaitUntilListeningAsync(
        ProtocolConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var probeAddress = GetLocalProbeAddress(configuration.BindAddress);
        var started = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(started) <= TimeSpan.FromSeconds(5))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(
                    probeAddress,
                    configuration.Port,
                    cancellationToken);
                return;
            }
            catch (SocketException)
            {
                await Task.Delay(25, cancellationToken);
            }
        }

        throw new TimeoutException("FTP listener did not start within 5 seconds.");
    }

    private static IPAddress GetLocalProbeAddress(string bindAddress)
    {
        var address = IPAddress.Parse(bindAddress);
        if (IPAddress.Any.Equals(address))
        {
            return IPAddress.Loopback;
        }

        if (IPAddress.IPv6Any.Equals(address))
        {
            return IPAddress.IPv6Loopback;
        }

        return address;
    }

    private async Task DisposeHostAsync(CancellationToken cancellationToken)
    {
        if (_host is not null)
        {
            await _host.StopAsync(cancellationToken);
            _host.Dispose();
        }

        _ftpServerHost = null;
        _host = null;
    }

    private async Task<System.Security.Cryptography.X509Certificates.X509Certificate2> GetCertificateAsync(
        CancellationToken cancellationToken)
    {
        if (_certificateManager is null || _certificateSettings is null)
        {
            throw new InvalidOperationException("FTPS requires a certificate manager and certificate settings.");
        }

        return await _certificateManager.GetOrCreateAsync(
            CertificatePurpose.Ftps,
            _certificateSettings,
            cancellationToken);
    }

    private void Publish(
        TransferEventKind kind,
        ProtocolConfiguration configuration,
        TransferResult result)
        => _eventBus.TryPublish(new TransferEvent(
            DateTimeOffset.UtcNow,
            Protocol,
            kind,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            result,
            $"{configuration.BindAddress}:{configuration.Port}",
            null));
}
