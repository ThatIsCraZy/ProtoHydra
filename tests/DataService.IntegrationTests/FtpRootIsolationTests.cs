using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using DataService.Core.Events;
using DataService.Infrastructure.Certificates;
using DataService.Protocols.Abstractions;
using DataService.Protocols.Ftp;
using FluentFTP;

namespace DataService.IntegrationTests;

public sealed class FtpRootIsolationTests : IDisposable
{
    private readonly string _workPath = Path.Combine(AppContext.BaseDirectory, "test-work", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(ProtocolKind.Ftp)]
    [InlineData(ProtocolKind.Ftps)]
    public async Task DotPaths_SupportListingDownloadAndUpload(ProtocolKind protocol)
    {
        var root = Path.Combine(_workPath, "root");
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        await File.WriteAllTextAsync(Path.Combine(root, "sub", "file.txt"), "dot-path-content");
        await File.WriteAllTextAsync(Path.Combine(root, "sub", "literal%20.txt"), "literal-name");
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var adapter = new FtpFileServerAdapter(protocol, new TransferEventBus(),
            certificateManager: new CertificateManager(),
            certificateSettings: new CertificateSettings(Path.Combine(_workPath, "certificates")));
        try
        {
            await adapter.StartAsync(new ProtocolConfiguration("127.0.0.1", port, root, true), CancellationToken.None);
            await using var client = new AsyncFtpClient("127.0.0.1", "test", "test", port, new FtpConfig
            {
                EncryptionMode = protocol == ProtocolKind.Ftps ? FtpEncryptionMode.Explicit : FtpEncryptionMode.None,
                ValidateAnyCertificate = true,
                SanitizeUrlEncoding = false
            });
            await client.Connect();
            await client.SetWorkingDirectory("./");
            Assert.Contains(await client.GetListing("./"), item => item.Name == "sub");
            Assert.Equal("dot-path-content"u8.ToArray(), await client.DownloadBytes("./sub/./file.txt", CancellationToken.None));
            Assert.Equal("literal-name"u8.ToArray(), await client.DownloadBytes("./sub/literal%20.txt", CancellationToken.None));

            await client.UploadBytes("uploaded"u8.ToArray(), "./sub/./upload.txt");
            Assert.Equal("uploaded", await File.ReadAllTextAsync(Path.Combine(root, "sub", "upload.txt")));
            await client.SetWorkingDirectory("./sub/.");
            Assert.Equal("dot-path-content"u8.ToArray(), await client.DownloadBytes("./file.txt", CancellationToken.None));
        }
        finally
        {
            await adapter.StopAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData(ProtocolKind.Ftp)]
    [InlineData(ProtocolKind.Ftps)]
    public async Task LinkedDirectory_CannotReadOrWriteOutsideRoot(ProtocolKind protocol)
    {
        var root = Path.Combine(_workPath, "root");
        var outside = Path.Combine(_workPath, "outside");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(Path.Combine(outside, "private.txt"), "outside-root");
        var link = Path.Combine(root, "linked");
        await CreateDirectoryLinkAsync(link, outside);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var bus = new TransferEventBus();
        var adapter = new FtpFileServerAdapter(protocol, bus,
            certificateManager: new CertificateManager(),
            certificateSettings: new CertificateSettings(Path.Combine(_workPath, "certificates")));

        try
        {
            await adapter.StartAsync(new ProtocolConfiguration("127.0.0.1", port, root, true), CancellationToken.None);
            await using var client = new AsyncFtpClient("127.0.0.1", "test", "test", port, new FtpConfig
            {
                EncryptionMode = protocol == ProtocolKind.Ftps ? FtpEncryptionMode.Explicit : FtpEncryptionMode.None,
                ValidateAnyCertificate = true,
                ConnectTimeout = 5000,
                ReadTimeout = 5000,
                DataConnectionConnectTimeout = 5000,
                DataConnectionReadTimeout = 5000
            });
            await client.Connect();

            Assert.DoesNotContain(await client.GetListing("./"), item => item.Name == "linked");

            var readError = await Record.ExceptionAsync(() => client.DownloadBytes("/linked/private.txt", CancellationToken.None));
            Assert.IsAssignableFrom<FluentFTP.Exceptions.FtpException>(readError);
            Assert.True(client.IsConnected);

            var writeError = await Record.ExceptionAsync(async () =>
            {
                await using var stream = await client.OpenWrite("/linked/new.txt");
                await stream.WriteAsync("forbidden"u8.ToArray());
            });
            Assert.IsAssignableFrom<FluentFTP.Exceptions.FtpException>(writeError);
            Assert.False(File.Exists(Path.Combine(outside, "new.txt")));
            Assert.Equal("outside-root", await File.ReadAllTextAsync(Path.Combine(outside, "private.txt")));

            var events = new List<TransferEvent>();
            while (bus.Reader.TryRead(out var item))
            {
                events.Add(item);
            }
            Assert.Contains(events, item => item.EventKind == TransferEventKind.RequestRejected);
        }
        finally
        {
            await adapter.StopAsync(CancellationToken.None);
            // Remove only the link before recursive fixture cleanup.
            Directory.Delete(link);
        }
    }

    private static async Task CreateDirectoryLinkAsync(string link, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(link, target);
            return;
        }

        // Junctions do not require the Windows symbolic-link privilege.
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add("$ErrorActionPreference = 'Stop'; New-Item -ItemType Junction -Path $env:HYDRA_TEST_LINK -Target $env:HYDRA_TEST_TARGET | Out-Null");
        start.Environment["HYDRA_TEST_LINK"] = link;
        start.Environment["HYDRA_TEST_TARGET"] = target;
        using var process = Process.Start(start)!;
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, error);
    }

    public void Dispose()
    {
        if (Directory.Exists(_workPath))
        {
            Directory.Delete(_workPath, recursive: true);
        }
    }
}
