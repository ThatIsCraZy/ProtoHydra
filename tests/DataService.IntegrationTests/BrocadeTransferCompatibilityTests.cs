using System.Net;
using System.Net.Sockets;
using System.Text;
using DataService.Core.Events;
using DataService.Infrastructure.Certificates;
using DataService.Protocols.Abstractions;
using DataService.Protocols.Ftp;
using DataService.Protocols.Ssh;
using FluentFTP;
using Renci.SshNet;
using SFTP.Enums;
using SFTP.Models;

namespace DataService.IntegrationTests;

public sealed class BrocadeTransferCompatibilityTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(
        AppContext.BaseDirectory, "test-work", "brocade", Guid.NewGuid().ToString("N"));

    public BrocadeTransferCompatibilityTests()
    {
        Directory.CreateDirectory(_rootPath);
    }

    [Theory]
    [InlineData(".", "/")]
    [InlineData("/.", "/")]
    [InlineData("/./", "/")]
    [InlineData("./sub/./ROFC05.txt", "/sub/ROFC05.txt")]
    [InlineData("/sub/./ROFC05.txt", "/sub/ROFC05.txt")]
    public async Task Sftp_RealPath_DotSegments_ReturnsCanonicalVirtualPath(string path, string expected)
    {
        var handler = new RootedSftpHandler(_rootPath, new TransferEventBus(), "test-user");

        // Brocade configdownload starts by resolving "/." after opening the subsystem.
        var resolved = await handler.RealPath(new SFTPPath(path));

        Assert.Equal(expected, resolved.Path);
    }

    [Theory]
    [InlineData("/./../outside.txt")]
    [InlineData("./sub/../../outside.txt")]
    [InlineData("//server/share/file.txt")]
    [InlineData("/./C:/Windows/win.ini")]
    [InlineData("/./sub\\outside.txt")]
    public async Task Sftp_RealPath_UnsafePaths_StillRejected(string path)
    {
        var handler = new RootedSftpHandler(_rootPath, new TransferEventBus(), "test-user");

        var exception = await Assert.ThrowsAsync<SftpStatusException>(
            () => handler.RealPath(new SFTPPath(path)));

        Assert.Equal(Status.PermissionDenied, exception.Status);
    }

    [Fact]
    public async Task Sftp_ConfigDownload_ResolvesRootDot_StatsAndDownloadsNestedFile()
    {
        await CreateConfigFileAsync();
        var bus = new TransferEventBus();
        var port = GetFreeTcpPort();
        var adapter = new SftpFileServerAdapter(bus, Path.Combine(_rootPath, "host-key"));
        await adapter.StartAsync(new ProtocolConfiguration("127.0.0.1", port, _rootPath, true), CancellationToken.None);
        try
        {
            using var client = new SftpClient("127.0.0.1", port, "test", "arbitrary-password")
            {
                OperationTimeout = TimeSpan.FromSeconds(5)
            };
            client.Connect();
            client.ChangeDirectory("/.");
            Assert.Equal("/", client.WorkingDirectory);
            Assert.Equal(150546, client.GetAttributes("./sub/ROFC05.txt").Size);
            using var output = new MemoryStream();
            client.DownloadFile("./sub/ROFC05.txt", output);
            Assert.Equal(150546, output.Length);
            Assert.All(output.ToArray(), value => Assert.Equal((byte)'x', value));
            client.Disconnect();

            var events = DrainEvents(bus);
            Assert.Contains(events, item => item.Command == "REALPATH" && item.RelativePath == "");
            Assert.Contains(events, item => item.EventKind == TransferEventKind.DownloadCompleted
                && item.RelativePath == "sub/ROFC05.txt" && item.ByteCount == 150546);
            Assert.DoesNotContain(events, item => item.EventKind == TransferEventKind.RequestRejected);
        }
        finally
        {
            await adapter.StopAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData(FtpDataConnectionType.PASV)]
    [InlineData(FtpDataConnectionType.EPSV)]
    [InlineData(FtpDataConnectionType.PORT)]
    [InlineData(FtpDataConnectionType.EPRT)]
    public async Task Ftp_ConfigDownload_ChangesDirectoryAndDownloadsNestedFile(FtpDataConnectionType mode)
    {
        await CreateConfigFileAsync();
        var bus = new TransferEventBus();
        var port = GetFreeTcpPort();
        var adapter = new FtpFileServerAdapter(bus);
        await adapter.StartAsync(new ProtocolConfiguration("127.0.0.1", port, _rootPath, true), CancellationToken.None);
        try
        {
            await using var client = new AsyncFtpClient("127.0.0.1", "test", "arbitrary-password", port,
                new FtpConfig { DataConnectionType = mode, ConnectTimeout = 5000, DataConnectionConnectTimeout = 5000 });
            await client.Connect();
            await client.SetWorkingDirectory("/.");
            await client.SetWorkingDirectory("sub");
            var bytes = await client.DownloadBytes("ROFC05.txt", CancellationToken.None);
            Assert.Equal(150546, bytes.Length);
            Assert.All(bytes, value => Assert.Equal((byte)'x', value));
            Assert.Contains(DrainEvents(bus), item => item.EventKind == TransferEventKind.DownloadCompleted
                && item.RelativePath == "sub/ROFC05.txt" && item.ByteCount == 150546);
        }
        finally
        {
            await adapter.StopAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData(ProtocolKind.Ftp, "PASV", false)]
    [InlineData(ProtocolKind.Ftp, "EPSV", false)]
    [InlineData(ProtocolKind.Ftp, "PASV", true)]
    [InlineData(ProtocolKind.Ftp, "EPSV", true)]
    [InlineData(ProtocolKind.Ftps, "PASV", true)]
    [InlineData(ProtocolKind.Ftps, "EPSV", true)]
    public async Task Ftp_PassiveListener_UsesConfiguredPortRange(ProtocolKind protocol, string command, bool customRange)
    {
        await CreateConfigFileAsync();
        var port = GetFreeTcpPort();
        var dataPort = GetFreeTcpPort();
        var configuration = new ProtocolConfiguration("127.0.0.1", port, _rootPath, true);
        if (customRange)
        {
            configuration = configuration with { FtpPassivePortRange = $"{dataPort}-{dataPort}" };
        }

        var adapter = new FtpFileServerAdapter(protocol, new TransferEventBus(),
            certificateManager: new CertificateManager(),
            certificateSettings: new CertificateSettings(Path.Combine(_rootPath, "certificates")));
        await adapter.StartAsync(configuration, CancellationToken.None);
        try
        {
            await using var client = new AsyncFtpClient("127.0.0.1", "test", "arbitrary-password", port,
                new FtpConfig
                {
                    EncryptionMode = protocol == ProtocolKind.Ftps ? FtpEncryptionMode.Explicit : FtpEncryptionMode.None,
                    ValidateAnyCertificate = true,
                    DataConnectionType = command == "PASV" ? FtpDataConnectionType.PASV : FtpDataConnectionType.EPSV
                });
            await client.Connect();
            await using var stream = Assert.IsType<FtpDataStream>(await client.OpenRead(
                "/sub/ROFC05.txt", FtpDataType.Binary, 0, false, CancellationToken.None));
            var endpoint = Assert.IsType<IPEndPoint>(stream.RemoteEndPoint);
            Assert.InRange(endpoint.Port, customRange ? dataPort : 50000, customRange ? dataPort : 50100);
            using var output = new MemoryStream();
            await stream.CopyToAsync(output);
            Assert.Equal(150546, output.Length);
        }
        finally
        {
            await adapter.StopAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-range")]
    [InlineData("50100-50000")]
    [InlineData("0-0")]
    [InlineData("50000-65536")]
    public async Task Ftp_InvalidPassiveRange_IsRejectedBeforeStarting(string range)
    {
        var adapter = new FtpFileServerAdapter(new TransferEventBus());
        var configuration = new ProtocolConfiguration("127.0.0.1", GetFreeTcpPort(), _rootPath, true)
        {
            FtpPassivePortRange = range
        };

        var result = await adapter.ValidateAsync(configuration, CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Contains("passive port range", result.Message);
        Assert.Equal(ProtocolRuntimeState.Stopped, adapter.State);
    }

    private Task CreateConfigFileAsync()
    {
        Directory.CreateDirectory(Path.Combine(_rootPath, "sub"));
        return File.WriteAllTextAsync(Path.Combine(_rootPath, "sub", "ROFC05.txt"), new string('x', 150546), Encoding.ASCII);
    }

    private static List<TransferEvent> DrainEvents(TransferEventBus bus)
    {
        var events = new List<TransferEvent>();
        while (bus.Reader.TryRead(out var item))
        {
            events.Add(item);
        }

        return events;
    }

    private static int GetFreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public void Dispose()
    {
        Directory.Delete(_rootPath, recursive: true);
    }
}
