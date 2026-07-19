using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using DataService.Core.Diagnostics;
using DataService.Core.Events;
using DataService.Infrastructure.Certificates;
using DataService.Protocols.Abstractions;
using DataService.Protocols.Ftp;
using DataService.Protocols.Http;
using DataService.Protocols.Ssh;
using DataService.Protocols.Tftp;
using FluentFTP;
using FluentFTP.Exceptions;
using Renci.SshNet;
using System.Text;
using Tftp.Net;

namespace DataService.IntegrationTests;

public sealed class ProtocolClientSmokeTests
{
    private static readonly string WorkspaceRoot = FindWorkspaceRoot();
    private static readonly string PublishRoot = Path.Combine(WorkspaceRoot, "root-Publish-Testfolder");

    [Fact]
    public async Task Http_DownloadsFile_WithHttpClient()
    {
        var rootPath = CreateProtocolRoot("http");
        await File.WriteAllTextAsync(Path.Combine(rootPath, "http.txt"), "http-ok");
        var port = GetFreeTcpPort();
        var adapter = new HttpFileServerAdapter(ProtocolKind.Http, new TransferEventBus());

        await adapter.StartAsync(new ProtocolConfiguration("127.0.0.1", port, rootPath, Enabled: true), CancellationToken.None);
        try
        {
            using var client = new HttpClient();
            using var response = await client.GetAsync($"http://127.0.0.1:{port}/http.txt");

            response.EnsureSuccessStatusCode();
            Assert.Equal("http-ok", await response.Content.ReadAsStringAsync());
        }
        finally
        {
            await adapter.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Https_DownloadsFile_WithHttpClient()
    {
        var rootPath = CreateProtocolRoot("https");
        await File.WriteAllTextAsync(Path.Combine(rootPath, "https.txt"), "https-ok");
        var port = GetFreeTcpPort();
        var certificateDirectory = Path.Combine(WorkspaceRoot, "test-artifacts", "certificates");
        var adapter = new HttpFileServerAdapter(
            ProtocolKind.Https,
            new TransferEventBus(),
            certificateManager: new CertificateManager(),
            certificateSettings: new CertificateSettings(certificateDirectory));

        await adapter.StartAsync(new ProtocolConfiguration("127.0.0.1", port, rootPath, Enabled: true), CancellationToken.None);
        try
        {
            using var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            };
            using var client = new HttpClient(handler);
            using var response = await client.GetAsync($"https://127.0.0.1:{port}/https.txt");

            response.EnsureSuccessStatusCode();
            Assert.Equal("https-ok", await response.Content.ReadAsStringAsync());
        }
        finally
        {
            await adapter.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Ftp_ListsDownloadsAndUploadsFile_WithFluentFtp()
    {
        var rootPath = CreateProtocolRoot("ftp");
        await File.WriteAllTextAsync(Path.Combine(rootPath, "ftp.txt"), "ftp-ok");
        Directory.CreateDirectory(Path.Combine(rootPath, "sub"));
        await File.WriteAllTextAsync(Path.Combine(rootPath, "sub", "nested.txt"), "nested-ok");
        var outsidePath = Path.Combine(Directory.GetParent(rootPath)!.FullName, "outside.txt");
        await File.WriteAllTextAsync(outsidePath, "outside-blocked");
        var port = GetFreeTcpPort();
        var eventBus = new TransferEventBus();
        var adapter = new FtpFileServerAdapter(eventBus);

        await adapter.StartAsync(new ProtocolConfiguration("127.0.0.1", port, rootPath, Enabled: true), CancellationToken.None);
        try
        {
            await using var client = new AsyncFtpClient("127.0.0.1", "any-user", "any-password", port);
            await client.Connect(CancellationToken.None);

            var listing = await client.GetListing("/", token: CancellationToken.None);
            Assert.Contains(listing, item => item.Name == "ftp.txt");

            var bytes = await client.DownloadBytes("/ftp.txt", CancellationToken.None);
            Assert.Equal("ftp-ok", Encoding.UTF8.GetString(bytes));

            var nestedBytes = await client.DownloadBytes("/sub/nested.txt", CancellationToken.None);
            Assert.Equal("nested-ok", Encoding.UTF8.GetString(nestedBytes));

            await client.UploadBytes(
                Encoding.UTF8.GetBytes("ftp-upload-ok"),
                "/uploaded.txt",
                token: CancellationToken.None);
            Assert.Equal("ftp-upload-ok", await File.ReadAllTextAsync(Path.Combine(rootPath, "uploaded.txt")));

            await Assert.ThrowsAnyAsync<FtpException>(
                () => client.DownloadBytes("/../outside.txt", CancellationToken.None));

            var events = await DrainEventsAsync(
                eventBus,
                items => HasFtpTrafficEvents(items, ProtocolKind.Ftp),
                TimeSpan.FromSeconds(5));
            Assert.Contains(events, item => item.Protocol == ProtocolKind.Ftp && item.EventKind == TransferEventKind.AuthenticationAttempt);
            Assert.Contains(events, item => item.Protocol == ProtocolKind.Ftp && item.EventKind == TransferEventKind.DirectoryListed);
            Assert.Contains(events, item => item.Protocol == ProtocolKind.Ftp && item.EventKind == TransferEventKind.DownloadCompleted && item.ByteCount > 0);
            Assert.True(
                events.Any(item => item.Protocol == ProtocolKind.Ftp && item.EventKind == TransferEventKind.UploadCompleted && item.ByteCount > 0),
                FormatEvents(events));
        }
        finally
        {
            await adapter.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Ftps_ListsDownloadsAndUploadsFile_WithFluentFtp()
    {
        var rootPath = CreateProtocolRoot("ftps");
        await File.WriteAllTextAsync(Path.Combine(rootPath, "ftps.txt"), "ftps-ok");
        Directory.CreateDirectory(Path.Combine(rootPath, "sub"));
        await File.WriteAllTextAsync(Path.Combine(rootPath, "sub", "nested-ftps.txt"), "nested-ftps-ok");
        var outsidePath = Path.Combine(Directory.GetParent(rootPath)!.FullName, "outside-ftps.txt");
        await File.WriteAllTextAsync(outsidePath, "outside-ftps-blocked");
        var port = GetFreeTcpPort();
        var certificateDirectory = Path.Combine(WorkspaceRoot, "test-artifacts", "certificates");
        var eventBus = new TransferEventBus();
        var adapter = new FtpFileServerAdapter(
            ProtocolKind.Ftps,
            eventBus,
            certificateManager: new CertificateManager(),
            certificateSettings: new CertificateSettings(certificateDirectory));
        var config = new FtpConfig
        {
            EncryptionMode = FtpEncryptionMode.Explicit,
            ValidateAnyCertificate = true
        };

        await adapter.StartAsync(new ProtocolConfiguration("127.0.0.1", port, rootPath, Enabled: true), CancellationToken.None);
        try
        {
            await using var client = new AsyncFtpClient("127.0.0.1", "any-user", "any-password", port, config);
            await client.Connect(CancellationToken.None);

            var listing = await client.GetListing("/", token: CancellationToken.None);
            Assert.Contains(listing, item => item.Name == "ftps.txt");

            var bytes = await client.DownloadBytes("/ftps.txt", CancellationToken.None);
            Assert.Equal("ftps-ok", Encoding.UTF8.GetString(bytes));

            var nestedBytes = await client.DownloadBytes("/sub/nested-ftps.txt", CancellationToken.None);
            Assert.Equal("nested-ftps-ok", Encoding.UTF8.GetString(nestedBytes));

            await client.UploadBytes(
                Encoding.UTF8.GetBytes("ftps-upload-ok"),
                "/uploaded-ftps.txt",
                token: CancellationToken.None);
            Assert.Equal("ftps-upload-ok", await File.ReadAllTextAsync(Path.Combine(rootPath, "uploaded-ftps.txt")));

            await Assert.ThrowsAnyAsync<FtpException>(
                () => client.DownloadBytes("/../outside-ftps.txt", CancellationToken.None));

            var events = await DrainEventsAsync(
                eventBus,
                items => HasFtpTrafficEvents(items, ProtocolKind.Ftps),
                TimeSpan.FromSeconds(5));
            Assert.Contains(events, item => item.Protocol == ProtocolKind.Ftps && item.EventKind == TransferEventKind.AuthenticationAttempt);
            Assert.Contains(events, item => item.Protocol == ProtocolKind.Ftps && item.EventKind == TransferEventKind.DirectoryListed);
            Assert.Contains(events, item => item.Protocol == ProtocolKind.Ftps && item.EventKind == TransferEventKind.DownloadCompleted && item.ByteCount > 0);
            Assert.True(
                events.Any(item => item.Protocol == ProtocolKind.Ftps && item.EventKind == TransferEventKind.UploadCompleted && item.ByteCount > 0),
                FormatEvents(events));
        }
        finally
        {
            await adapter.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Tftp_DownloadsAndUploadsFile_WithTftpNetClient()
    {
        var rootPath = CreateProtocolRoot("tftp");
        await File.WriteAllTextAsync(Path.Combine(rootPath, "tftp.txt"), "tftp-ok");
        Directory.CreateDirectory(Path.Combine(rootPath, "sub"));
        await File.WriteAllTextAsync(Path.Combine(rootPath, "sub", "nested-tftp.txt"), "nested-tftp-ok");
        var outsidePath = Path.Combine(Directory.GetParent(rootPath)!.FullName, "outside-tftp.txt");
        await File.WriteAllTextAsync(outsidePath, "outside-tftp-blocked");
        var port = GetFreeUdpPort();
        var adapter = new TftpFileServerAdapter(new TransferEventBus());

        await adapter.StartAsync(new ProtocolConfiguration("127.0.0.1", port, rootPath, Enabled: true), CancellationToken.None);
        try
        {
            var client = new TftpClient(IPAddress.Loopback, port);

            var bytes = await DownloadTftpFileAsync(client, "tftp.txt");
            Assert.Equal("tftp-ok", Encoding.UTF8.GetString(bytes));

            var nestedBytes = await DownloadTftpFileAsync(client, "sub/nested-tftp.txt");
            Assert.Equal("nested-tftp-ok", Encoding.UTF8.GetString(nestedBytes));

            await UploadTftpFileAsync(client, "uploaded-tftp.txt", Encoding.UTF8.GetBytes("tftp-upload-ok"));
            var uploadPath = Path.Combine(rootPath, "uploaded-tftp.txt");
            await WaitForFileAsync(uploadPath);
            Assert.Equal("tftp-upload-ok", await File.ReadAllTextAsync(uploadPath));

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => DownloadTftpFileAsync(client, "../outside-tftp.txt"));
        }
        finally
        {
            await adapter.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Sftp_ListsDownloadsAndUploadsFile_WithSshNet()
    {
        var rootPath = CreateProtocolRoot("sftp");
        File.WriteAllText(Path.Combine(rootPath, "sftp.txt"), "sftp-ok");
        Directory.CreateDirectory(Path.Combine(rootPath, "sub"));
        File.WriteAllText(Path.Combine(rootPath, "sub", "nested-sftp.txt"), "nested-sftp-ok");
        var outsidePath = Path.Combine(Directory.GetParent(rootPath)!.FullName, "outside-sftp.txt");
        File.WriteAllText(outsidePath, "outside-sftp-blocked");
        var port = GetFreeTcpPort();
        var hostKeyDirectory = Path.Combine(WorkspaceRoot, "test-artifacts", "ssh");
        var eventBus = new TransferEventBus();
        var adapter = new SftpFileServerAdapter(eventBus, hostKeyDirectory);

        await adapter.StartAsync(new ProtocolConfiguration("127.0.0.1", port, rootPath, Enabled: true), CancellationToken.None);
        try
        {
            using var client = new SftpClient("127.0.0.1", port, "any-user", "any-password");
            client.Connect();

            var listing = client.ListDirectory("/");
            Assert.Contains(listing, item => item.Name == "sftp.txt");

            using var output = new MemoryStream();
            client.DownloadFile("/sftp.txt", output);
            Assert.Equal("sftp-ok", Encoding.UTF8.GetString(output.ToArray()));

            using var nestedOutput = new MemoryStream();
            client.DownloadFile("/sub/nested-sftp.txt", nestedOutput);
            Assert.Equal("nested-sftp-ok", Encoding.UTF8.GetString(nestedOutput.ToArray()));

            using var upload = new MemoryStream(Encoding.UTF8.GetBytes("sftp-upload-ok"));
            client.UploadFile(upload, "/uploaded-sftp.txt", canOverride: true);
            Assert.Equal("sftp-upload-ok", await File.ReadAllTextAsync(Path.Combine(rootPath, "uploaded-sftp.txt")));

            var events = await DrainEventsAsync(
                eventBus,
                items => HasSshTransferEvents(items, ProtocolKind.Sftp),
                TimeSpan.FromSeconds(5));
            Assert.True(
                HasSshTransferEvents(events, ProtocolKind.Sftp),
                FormatEvents(events));

            Assert.ThrowsAny<Exception>(() => client.DownloadFile("/../outside-sftp.txt", new MemoryStream()));
            Assert.ThrowsAny<Exception>(() =>
            {
                using var escapeUpload = new MemoryStream(Encoding.UTF8.GetBytes("escape"));
                client.UploadFile(escapeUpload, "/../outside-sftp.txt", canOverride: true);
            });
        }
        finally
        {
            await adapter.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Scp_DownloadsAndUploadsFile_WithSshNet()
    {
        var rootPath = CreateProtocolRoot("scp");
        await File.WriteAllTextAsync(Path.Combine(rootPath, "scp.txt"), "scp-ok");
        var outsidePath = Path.Combine(Directory.GetParent(rootPath)!.FullName, "outside-scp.txt");
        await File.WriteAllTextAsync(outsidePath, "outside-scp-blocked");
        var port = GetFreeTcpPort();
        var hostKeyDirectory = Path.Combine(WorkspaceRoot, "test-artifacts", "ssh");
        var eventBus = new TransferEventBus();
        var adapter = new ScpFileServerAdapter(eventBus, hostKeyDirectory);

        await adapter.StartAsync(new ProtocolConfiguration("127.0.0.1", port, rootPath, Enabled: true), CancellationToken.None);
        try
        {
            using var client = new ScpClient("127.0.0.1", port, "any-user", "any-password");
            client.Connect();

            using var output = new MemoryStream();
            client.Download("/scp.txt", output);
            Assert.Equal("scp-ok", Encoding.UTF8.GetString(output.ToArray()));

            using var upload = new MemoryStream(Encoding.UTF8.GetBytes("scp-upload-ok"));
            client.Upload(upload, "/uploaded-scp.txt");
            Assert.Equal("scp-upload-ok", await File.ReadAllTextAsync(Path.Combine(rootPath, "uploaded-scp.txt")));

            var sourceDirectory = Path.Combine(rootPath, "scp-source");
            Directory.CreateDirectory(Path.Combine(sourceDirectory, "child"));
            await File.WriteAllTextAsync(Path.Combine(sourceDirectory, "root-file.txt"), "scp-root");
            await File.WriteAllTextAsync(Path.Combine(sourceDirectory, "child", "child-file.txt"), "scp-child");
            var preservedTime = new DateTime(2024, 02, 03, 04, 05, 06, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(Path.Combine(sourceDirectory, "child", "child-file.txt"), preservedTime);

            using var sourceClient = new ScpClient("127.0.0.1", port, "any-user", "any-password");
            sourceClient.Connect();
            Directory.CreateDirectory(Path.Combine(rootPath, "uploaded-tree"));
            try
            {
                sourceClient.Upload(new DirectoryInfo(sourceDirectory), "/uploaded-tree");
            }
            catch (Exception ex)
            {
                throw new Exception($"SCP recursive upload failed: {ex.Message}{Environment.NewLine}{DrainEvents(eventBus)}", ex);
            }
            Assert.Equal("scp-root", await File.ReadAllTextAsync(Path.Combine(rootPath, "uploaded-tree", "root-file.txt")));
            var uploadedChildPath = Path.Combine(rootPath, "uploaded-tree", "child", "child-file.txt");
            Assert.Equal("scp-child", await File.ReadAllTextAsync(uploadedChildPath));
            var timestampDelta = (File.GetLastWriteTimeUtc(uploadedChildPath) - preservedTime).Duration();
            Assert.True(timestampDelta < TimeSpan.FromSeconds(2), $"Expected preserved SCP timestamp. Delta: {timestampDelta}.");

            var downloadTarget = Path.Combine(rootPath, "scp-downloaded");
            Directory.CreateDirectory(downloadTarget);
            sourceClient.Download("/uploaded-tree", new DirectoryInfo(downloadTarget));
            Assert.Equal("scp-child", await File.ReadAllTextAsync(Path.Combine(downloadTarget, "child", "child-file.txt")));

            Assert.ThrowsAny<Exception>(() => client.Download("/../outside-scp.txt", new MemoryStream()));
        }
        finally
        {
            await adapter.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Sftp_SupportsUploadWorkflow_MkdirSetstatRenameFstat_WithSshNet()
    {
        var rootPath = CreateProtocolRoot("sftp-upload");
        var port = GetFreeTcpPort();
        var hostKeyDirectory = Path.Combine(WorkspaceRoot, "test-artifacts", "ssh");
        var eventBus = new TransferEventBus();
        var adapter = new SftpFileServerAdapter(eventBus, hostKeyDirectory);

        await adapter.StartAsync(new ProtocolConfiguration("127.0.0.1", port, rootPath, Enabled: true), CancellationToken.None);
        try
        {
            using var client = new SftpClient("127.0.0.1", port, "any-user", "any-password");
            client.Connect();

            client.CreateDirectory("/upload-dir");
            Assert.True(Directory.Exists(Path.Combine(rootPath, "upload-dir")));

            using (var upload = new MemoryStream(Encoding.UTF8.GetBytes("sftp-workflow-ok")))
            {
                client.UploadFile(upload, "/upload-dir/data.txt.part", canOverride: true);
            }

            var preservedTime = new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc);
            client.SetLastWriteTimeUtc("/upload-dir/data.txt.part", preservedTime);

            client.RenameFile("/upload-dir/data.txt.part", "/upload-dir/data.txt");
            var finalPath = Path.Combine(rootPath, "upload-dir", "data.txt");
            Assert.Equal("sftp-workflow-ok", await File.ReadAllTextAsync(finalPath));
            Assert.False(File.Exists(Path.Combine(rootPath, "upload-dir", "data.txt.part")));
            var timestampDelta = (File.GetLastWriteTimeUtc(finalPath) - preservedTime).Duration();
            Assert.True(timestampDelta < TimeSpan.FromSeconds(2), $"Expected preserved SFTP timestamp. Delta: {timestampDelta}.");

            using (var remote = client.Open("/upload-dir/fstat.bin", FileMode.Create, FileAccess.Write))
            {
                var payload = new byte[1234];
                remote.Write(payload, 0, payload.Length);
                remote.Flush();
                Assert.Equal(payload.Length, remote.Length);
            }

            Assert.Equal(1234, new FileInfo(Path.Combine(rootPath, "upload-dir", "fstat.bin")).Length);

            Assert.ThrowsAny<Exception>(() => client.RenameFile("/upload-dir/fstat.bin", "/upload-dir/data.txt"));
            Assert.ThrowsAny<Exception>(() => client.CreateDirectory("/upload-dir"));
            Assert.ThrowsAny<Exception>(() => client.CreateDirectory("/missing-parent/child"));
            Assert.ThrowsAny<Exception>(() => client.RenameFile("/upload-dir/data.txt", "/../escaped.txt"));

            // A lookup on a missing path must be visible in the event stream (capture log).
            Assert.ThrowsAny<Exception>(() => client.GetAttributes("/does-not-exist.bin"));

            var events = await DrainEventsAsync(
                eventBus,
                items => items.Any(item => item.Command == "RENAME" && item.Result == TransferResult.Success)
                    && items.Any(item => item.Result == TransferResult.Failed && item.RelativePath == "does-not-exist.bin"),
                TimeSpan.FromSeconds(5));
            Assert.Contains(events, item => item.Command == "MKDIR" && item.Result == TransferResult.Success);
            Assert.Contains(events, item => item.Command == "SETSTAT" && item.Result == TransferResult.Success);
            Assert.Contains(events, item => item.Command == "RENAME" && item.Result == TransferResult.Success);
            Assert.Contains(events, item => item.Result == TransferResult.Failed
                && item.RelativePath == "does-not-exist.bin"
                && item.Message == "No such file or directory.");
            Assert.Contains(events, item => Equals(item.SourceAddress, IPAddress.Loopback));
        }
        finally
        {
            await adapter.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Sftp_ListingSurvivesDeniedEntry_AndSessionStaysUsable()
    {
        var rootPath = CreateProtocolRoot("sftp-acl");
        File.WriteAllText(Path.Combine(rootPath, "ok.txt"), "acl-ok");
        var deniedPath = Path.Combine(rootPath, "denied.txt");
        File.WriteAllText(deniedPath, "secret");
        var port = GetFreeTcpPort();
        var hostKeyDirectory = Path.Combine(WorkspaceRoot, "test-artifacts", "ssh");
        var eventBus = new TransferEventBus();
        var adapter = new SftpFileServerAdapter(eventBus, hostKeyDirectory);

        DenyReadAccess(deniedPath);
        await adapter.StartAsync(new ProtocolConfiguration("127.0.0.1", port, rootPath, Enabled: true), CancellationToken.None);
        try
        {
            using var client = new SftpClient("127.0.0.1", port, "any-user", "any-password");
            client.Connect();

            var listing = client.ListDirectory("/").Select(item => item.Name).ToArray();
            Assert.Contains("ok.txt", listing);
            Assert.Contains("denied.txt", listing);

            Assert.ThrowsAny<Exception>(() => client.DownloadFile("/denied.txt", new MemoryStream()));

            using var output = new MemoryStream();
            client.DownloadFile("/ok.txt", output);
            Assert.Equal("acl-ok", Encoding.UTF8.GetString(output.ToArray()));

            var events = await DrainEventsAsync(
                eventBus,
                items => items.Any(item => item.IoError == IoErrorCategory.AccessDenied),
                TimeSpan.FromSeconds(5));
            Assert.Contains(events, item => item.IoError == IoErrorCategory.AccessDenied);
        }
        finally
        {
            await adapter.StopAsync(CancellationToken.None);
            RemoveDenyAccess(deniedPath);
        }
    }

    [Fact]
    public async Task Sftp_UploadOntoReadOnlyTarget_FailsCleanly_AndLeavesNoTempFiles()
    {
        var rootPath = CreateProtocolRoot("sftp-readonly");
        var targetPath = Path.Combine(rootPath, "target.txt");
        File.WriteAllText(targetPath, "original");
        File.SetAttributes(targetPath, FileAttributes.ReadOnly);
        var port = GetFreeTcpPort();
        var hostKeyDirectory = Path.Combine(WorkspaceRoot, "test-artifacts", "ssh");
        var adapter = new SftpFileServerAdapter(new TransferEventBus(), hostKeyDirectory);

        await adapter.StartAsync(new ProtocolConfiguration("127.0.0.1", port, rootPath, Enabled: true), CancellationToken.None);
        try
        {
            using var client = new SftpClient("127.0.0.1", port, "any-user", "any-password");
            client.Connect();

            Assert.ThrowsAny<Exception>(() =>
            {
                using var upload = new MemoryStream(Encoding.UTF8.GetBytes("overwrite-attempt"));
                client.UploadFile(upload, "/target.txt", canOverride: true);
            });

            Assert.Equal("original", File.ReadAllText(targetPath));
            Assert.Empty(Directory.GetFiles(rootPath, "*.uploading"));

            var listing = client.ListDirectory("/").Select(item => item.Name).ToArray();
            Assert.Contains("target.txt", listing);
        }
        finally
        {
            await adapter.StopAsync(CancellationToken.None);
            File.SetAttributes(targetPath, FileAttributes.Normal);
        }
    }

    [Fact]
    public async Task Http_DeniedFile_Returns500_AndServerStaysUp()
    {
        var rootPath = CreateProtocolRoot("http-acl");
        await File.WriteAllTextAsync(Path.Combine(rootPath, "ok.txt"), "http-acl-ok");
        var deniedPath = Path.Combine(rootPath, "denied.txt");
        await File.WriteAllTextAsync(deniedPath, "secret");
        var port = GetFreeTcpPort();
        var eventBus = new TransferEventBus();
        var adapter = new HttpFileServerAdapter(ProtocolKind.Http, eventBus);

        DenyReadAccess(deniedPath);
        await adapter.StartAsync(new ProtocolConfiguration("127.0.0.1", port, rootPath, Enabled: true), CancellationToken.None);
        try
        {
            using var client = new HttpClient();

            using var deniedResponse = await client.GetAsync($"http://127.0.0.1:{port}/denied.txt");
            Assert.Equal(HttpStatusCode.InternalServerError, deniedResponse.StatusCode);

            using var okResponse = await client.GetAsync($"http://127.0.0.1:{port}/ok.txt");
            Assert.Equal(HttpStatusCode.OK, okResponse.StatusCode);
            Assert.Equal("http-acl-ok", await okResponse.Content.ReadAsStringAsync());

            var listingResponse = await client.GetStringAsync($"http://127.0.0.1:{port}/");
            Assert.Contains("ok.txt", listingResponse);

            var events = await DrainEventsAsync(
                eventBus,
                items => items.Any(item => item.IoError == IoErrorCategory.AccessDenied),
                TimeSpan.FromSeconds(5));
            Assert.Contains(events, item => item.IoError == IoErrorCategory.AccessDenied && item.EventKind == TransferEventKind.DownloadFailed);
        }
        finally
        {
            await adapter.StopAsync(CancellationToken.None);
            RemoveDenyAccess(deniedPath);
        }
    }

    [Fact]
    public async Task Sftp_ServerAnswersChannelClose_SoClientsDoNotStallOnTimeout()
    {
        // Regression guard for the Brocade FOS 30s-per-file stall: after the SFTP session
        // ends, the server must answer the client's CHANNEL_CLOSE with its own EOF +
        // CHANNEL_CLOSE (exit-status 0). SSH.NET waits up to ChannelCloseTimeout for that
        // answer, exactly like FOS does — without the fix this disconnect blocks for the
        // full timeout instead of returning immediately.
        var rootPath = CreateProtocolRoot("sftp-close");
        await File.WriteAllTextAsync(Path.Combine(rootPath, "close.txt"), "close-ok");
        var port = GetFreeTcpPort();
        var hostKeyDirectory = Path.Combine(WorkspaceRoot, "test-artifacts", "ssh");
        var adapter = new SftpFileServerAdapter(new TransferEventBus(), hostKeyDirectory);

        await adapter.StartAsync(new ProtocolConfiguration("127.0.0.1", port, rootPath, Enabled: true), CancellationToken.None);
        try
        {
            var connectionInfo = new ConnectionInfo(
                "127.0.0.1",
                port,
                "any-user",
                new PasswordAuthenticationMethod("any-user", "any-password"))
            {
                ChannelCloseTimeout = TimeSpan.FromSeconds(10)
            };
            using var client = new SftpClient(connectionInfo);
            client.Connect();
            using (var output = new MemoryStream())
            {
                client.DownloadFile("/close.txt", output);
                Assert.Equal("close-ok", Encoding.UTF8.GetString(output.ToArray()));
            }

            var stopwatch = Stopwatch.StartNew();
            client.Disconnect();
            stopwatch.Stop();

            Assert.True(
                stopwatch.Elapsed < TimeSpan.FromSeconds(5),
                $"Disconnect took {stopwatch.Elapsed.TotalSeconds:0.0}s — the server did not answer the client's "
                + "CHANNEL_CLOSE, so the client waited for its ChannelCloseTimeout (10s). "
                + "Clients like Brocade FOS firmwaredownload stall ~30s per file on this.");
        }
        finally
        {
            await adapter.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Scp_UploadsDirectoryTreeToNewTargetName_WithRawScpSink()
    {
        var rootPath = CreateProtocolRoot("scp-rename");
        var port = GetFreeTcpPort();
        var hostKeyDirectory = Path.Combine(WorkspaceRoot, "test-artifacts", "ssh");
        var adapter = new ScpFileServerAdapter(new TransferEventBus(), hostKeyDirectory);

        await adapter.StartAsync(new ProtocolConfiguration("127.0.0.1", port, rootPath, Enabled: true), CancellationToken.None);
        try
        {
            using var ssh = new SshClient("127.0.0.1", port, "any-user", "any-password");
            ssh.Connect();

            using var command = ssh.CreateCommand("scp -t -r /renamed-tree");
            var asyncResult = command.BeginExecute();
            using (var input = command.CreateInputStream())
            {
                var output = command.OutputStream;
                AssertScpOk(output);

                SendScpLine(input, "D0755 0 source");
                AssertScpOk(output);

                SendScpLine(input, "C0644 8 leaf.txt");
                AssertScpOk(output);

                var payload = Encoding.UTF8.GetBytes("scp-tree");
                input.Write(payload, 0, payload.Length);
                input.WriteByte(0);
                input.Flush();
                AssertScpOk(output);

                SendScpLine(input, "E");
                AssertScpOk(output);
            }

            command.EndExecute(asyncResult);

            Assert.Equal("scp-tree", await File.ReadAllTextAsync(Path.Combine(rootPath, "renamed-tree", "leaf.txt")));
            Assert.False(Directory.Exists(Path.Combine(rootPath, "renamed-tree", "source")));
        }
        finally
        {
            await adapter.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Scp_TreatsWarningResponseAsNonFatal_WithRawScpSource()
    {
        var rootPath = CreateProtocolRoot("scp-warning");
        await File.WriteAllTextAsync(Path.Combine(rootPath, "warn.txt"), "warn-ok");
        var port = GetFreeTcpPort();
        var hostKeyDirectory = Path.Combine(WorkspaceRoot, "test-artifacts", "ssh");
        var adapter = new ScpFileServerAdapter(new TransferEventBus(), hostKeyDirectory);

        await adapter.StartAsync(new ProtocolConfiguration("127.0.0.1", port, rootPath, Enabled: true), CancellationToken.None);
        try
        {
            using var ssh = new SshClient("127.0.0.1", port, "any-user", "any-password");
            ssh.Connect();

            using var command = ssh.CreateCommand("scp -f /warn.txt");
            var asyncResult = command.BeginExecute();
            using (var input = command.CreateInputStream())
            {
                var output = command.OutputStream;

                input.WriteByte(1);
                var warning = Encoding.UTF8.GetBytes("integration-test warning\n");
                input.Write(warning, 0, warning.Length);
                input.Flush();

                var header = ReadScpLine(output);
                Assert.Equal("C0644 7 warn.txt", header);

                input.WriteByte(0);
                input.Flush();

                var payload = ReadExact(output, 7);
                Assert.Equal("warn-ok", Encoding.UTF8.GetString(payload));
                AssertScpOk(output);

                input.WriteByte(0);
                input.Flush();
            }

            command.EndExecute(asyncResult);
        }
        finally
        {
            await adapter.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Scp_RejectsInvalidExecCommand_WithScpErrorRecord()
    {
        var rootPath = CreateProtocolRoot("scp-reject");
        var port = GetFreeTcpPort();
        var hostKeyDirectory = Path.Combine(WorkspaceRoot, "test-artifacts", "ssh");
        var adapter = new ScpFileServerAdapter(new TransferEventBus(), hostKeyDirectory);

        await adapter.StartAsync(new ProtocolConfiguration("127.0.0.1", port, rootPath, Enabled: true), CancellationToken.None);
        try
        {
            using var ssh = new SshClient("127.0.0.1", port, "any-user", "any-password");
            ssh.Connect();

            using var command = ssh.CreateCommand("scp -t");
            var result = command.Execute();

            Assert.True(command.ExitStatus == 1, $"Expected exit status 1, got {command.ExitStatus?.ToString() ?? "null"}.");
            Assert.Contains("scp:", result);
        }
        finally
        {
            await adapter.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ScpShell_AcceptsShellChannel_AndAnswersWinScpCommands_WithSshNet()
    {
        var rootPath = CreateProtocolRoot("scp-shell-e2e");
        await File.WriteAllTextAsync(Path.Combine(rootPath, "shell.txt"), "shell-ok");
        Directory.CreateDirectory(Path.Combine(rootPath, "folder"));
        var port = GetFreeTcpPort();
        var hostKeyDirectory = Path.Combine(WorkspaceRoot, "test-artifacts", "ssh");
        var adapter = new ScpFileServerAdapter(new TransferEventBus(), hostKeyDirectory);

        await adapter.StartAsync(new ProtocolConfiguration("127.0.0.1", port, rootPath, Enabled: true), CancellationToken.None);
        try
        {
            using var ssh = new SshClient("127.0.0.1", port, "any-user", "any-password");
            ssh.Connect();
            using var shell = ssh.CreateShellStream("dumb", 80, 24, 800, 600, 8192);

            shell.Write($"echo \"{ShellBegin}\" ; pwd ; echo \"{ShellEnd}$?\"\n");
            shell.Flush();
            var (pwdOutput, pwdCode) = ReadWinScpResponse(shell, TimeSpan.FromSeconds(15));
            Assert.Equal("/", pwdOutput.Trim());
            Assert.Equal(0, pwdCode);

            shell.Write($"echo \"{ShellBegin}\" ; ls -la \"/\" ; echo \"{ShellEnd}$?\"\n");
            shell.Flush();
            var (listing, lsCode) = ReadWinScpResponse(shell, TimeSpan.FromSeconds(15));
            Assert.Equal(0, lsCode);
            Assert.Contains("shell.txt", listing);
            Assert.Contains("folder", listing);
        }
        finally
        {
            await adapter.StopAsync(CancellationToken.None);
        }
    }

    private const string ShellBegin = "WinSCP: this is begin-of-file";
    private const string ShellEnd = "WinSCP: this is end-of-file:";

    private static (string Output, int Code) ReadWinScpResponse(ShellStream shell, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        var started = false;
        var output = new StringBuilder();
        while (DateTimeOffset.UtcNow < deadline)
        {
            var line = shell.ReadLine(TimeSpan.FromMilliseconds(500));
            if (line is null)
            {
                continue;
            }

            line = line.TrimEnd('\r');
            if (!started)
            {
                if (line.Contains(ShellBegin, StringComparison.Ordinal))
                {
                    started = true;
                }

                continue;
            }

            var index = line.IndexOf(ShellEnd, StringComparison.Ordinal);
            if (index >= 0)
            {
                var code = int.TryParse(line[(index + ShellEnd.Length)..].Trim(), out var parsed) ? parsed : -1;
                return (output.ToString(), code);
            }

            output.Append(line).Append('\n');
        }

        throw new TimeoutException($"No WinSCP end marker within {timeout.TotalSeconds}s. Collected: {output}");
    }

    [Fact]
    public async Task SharedSshListener_AllowsSftpSubsystemAndScpExec_OnSamePort_WithSshNet()
    {
        var rootPath = CreateProtocolRoot("ssh-shared");
        await File.WriteAllTextAsync(Path.Combine(rootPath, "shared-sftp.txt"), "shared-sftp-ok");
        await File.WriteAllTextAsync(Path.Combine(rootPath, "shared-scp.txt"), "shared-scp-ok");
        var port = GetFreeTcpPort();
        var hostKeyDirectory = Path.Combine(WorkspaceRoot, "test-artifacts", "ssh", Guid.NewGuid().ToString("N"));
        var eventBus = new TransferEventBus();
        var sharedServer = new SharedSshServer(eventBus, hostKeyDirectory);
        var sftpAdapter = new SftpFileServerAdapter(sharedServer);
        var scpAdapter = new ScpFileServerAdapter(sharedServer);
        var configuration = new ProtocolConfiguration("127.0.0.1", port, rootPath, Enabled: true);

        await sftpAdapter.StartAsync(configuration, CancellationToken.None);
        await scpAdapter.StartAsync(configuration, CancellationToken.None);
        try
        {
            Assert.Equal(ProtocolRuntimeState.Running, sftpAdapter.State);
            Assert.Equal(ProtocolRuntimeState.Running, scpAdapter.State);

            using (var probe = new TcpClient())
            {
                await probe.ConnectAsync(IPAddress.Loopback, port);
            }

            var probeEvents = await DrainEventsAsync(
                eventBus,
                items => items.Any(item => item.EventKind == TransferEventKind.ListenerFaulted),
                TimeSpan.FromMilliseconds(500));
            Assert.DoesNotContain(
                probeEvents,
                item => item.EventKind == TransferEventKind.ListenerFaulted
                    && item.Protocol is ProtocolKind.Sftp or ProtocolKind.Scp);
            Assert.Equal(ProtocolRuntimeState.Running, sftpAdapter.State);
            Assert.Equal(ProtocolRuntimeState.Running, scpAdapter.State);

            using (var sftpClient = new SftpClient("127.0.0.1", port, "any-user", "any-password"))
            {
                sftpClient.Connect();
                var listing = sftpClient.ListDirectory("/");
                Assert.Contains(listing, item => item.Name == "shared-sftp.txt");

                using var output = new MemoryStream();
                sftpClient.DownloadFile("/shared-sftp.txt", output);
                Assert.Equal("shared-sftp-ok", Encoding.UTF8.GetString(output.ToArray()));
            }

            using (var scpClient = new ScpClient("127.0.0.1", port, "any-user", "any-password"))
            {
                scpClient.Connect();

                using var output = new MemoryStream();
                scpClient.Download("/shared-scp.txt", output);
                Assert.Equal("shared-scp-ok", Encoding.UTF8.GetString(output.ToArray()));

                using var upload = new MemoryStream(Encoding.UTF8.GetBytes("shared-scp-upload-ok"));
                scpClient.Upload(upload, "/shared-upload.txt");
                Assert.Equal("shared-scp-upload-ok", await File.ReadAllTextAsync(Path.Combine(rootPath, "shared-upload.txt")));
            }
        }
        finally
        {
            await scpAdapter.StopAsync(CancellationToken.None);
            await sftpAdapter.StopAsync(CancellationToken.None);
        }
    }

    private static string CreateProtocolRoot(string protocol)
    {
        var path = Path.Combine(PublishRoot, protocol, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static int GetFreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static int GetFreeUdpPort()
    {
        using var listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)listener.Client.LocalEndPoint!).Port;
    }

    private static async Task<byte[]> DownloadTftpFileAsync(TftpClient client, string fileName)
    {
        await using var output = new MemoryStream();
        var transfer = client.Download(fileName);
        await RunTftpTransferAsync(transfer, output);
        return output.ToArray();
    }

    private static async Task UploadTftpFileAsync(TftpClient client, string fileName, byte[] content)
    {
        await using var input = new MemoryStream(content);
        var transfer = client.Upload(fileName);
        await RunTftpTransferAsync(transfer, input);
    }

    private static async Task RunTftpTransferAsync(ITftpTransfer transfer, Stream stream)
    {
        transfer.TransferMode = TftpTransferMode.octet;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transfer.OnFinished += _ => completion.TrySetResult();
        transfer.OnError += (_, error) => completion.TrySetException(new InvalidOperationException(error.ToString()));
        transfer.Start(stream);

        var completed = await Task.WhenAny(completion.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        if (completed != completion.Task)
        {
            transfer.Cancel(new TftpErrorPacket(4, "Test timeout."));
            throw new TimeoutException($"TFTP transfer timed out for {transfer.Filename}.");
        }

        await completion.Task;
    }

    private static async Task WaitForFileAsync(string path)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (File.Exists(path))
            {
                try
                {
                    await using var stream = new FileStream(
                        path,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read);
                    return;
                }
                catch (IOException)
                {
                }
            }

            await Task.Delay(25);
        }

        throw new FileNotFoundException("Expected file was not created before the timeout.", path);
    }

    private static void DenyReadAccess(string path)
        => RunIcacls(path, $"/deny \"{Environment.UserName}:(R)\"");

    private static void RemoveDenyAccess(string path)
        => RunIcacls(path, $"/remove:d \"{Environment.UserName}\"");

    private static void RunIcacls(string path, string arguments)
    {
        var startInfo = new ProcessStartInfo("icacls", $"\"{path}\" {arguments}")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        using var process = Process.Start(startInfo)!;
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"icacls failed ({process.ExitCode}): {process.StandardError.ReadToEnd()}{process.StandardOutput.ReadToEnd()}");
        }
    }

    private static void SendScpLine(Stream input, string line)
    {
        var data = Encoding.UTF8.GetBytes(line + "\n");
        input.Write(data, 0, data.Length);
        input.Flush();
    }

    private static void AssertScpOk(Stream output)
        => Assert.Equal(0, ReadExact(output, 1)[0]);

    private static string ReadScpLine(Stream output)
    {
        var line = new List<byte>();
        while (true)
        {
            var next = ReadExact(output, 1)[0];
            if (next == (byte)'\n')
            {
                return Encoding.UTF8.GetString(line.ToArray());
            }

            line.Add(next);
        }
    }

    private static byte[] ReadExact(Stream output, int count)
    {
        var buffer = new byte[count];
        var offset = 0;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (offset < count)
        {
            var read = output.Read(buffer, offset, count - offset);
            if (read > 0)
            {
                offset += read;
                continue;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException($"Timed out reading {count} bytes from the SCP channel.");
            }

            Thread.Sleep(20);
        }

        return buffer;
    }

    private static bool HasFtpTrafficEvents(IReadOnlyList<TransferEvent> events, ProtocolKind protocol)
        => events.Any(item => item.Protocol == protocol && item.EventKind == TransferEventKind.AuthenticationAttempt)
            && events.Any(item => item.Protocol == protocol && item.EventKind == TransferEventKind.DirectoryListed)
            && events.Any(item => item.Protocol == protocol && item.EventKind == TransferEventKind.DownloadCompleted && item.ByteCount > 0)
            && events.Any(item => item.Protocol == protocol && item.EventKind == TransferEventKind.UploadCompleted && item.ByteCount > 0);

    private static bool HasSshTransferEvents(IReadOnlyList<TransferEvent> events, ProtocolKind protocol)
        => events.Any(item => item.Protocol == protocol && item.EventKind == TransferEventKind.DownloadCompleted && item.ByteCount > 0)
            && events.Any(item => item.Protocol == protocol && item.EventKind == TransferEventKind.UploadCompleted && item.ByteCount > 0);

    private static string FormatEvents(IEnumerable<TransferEvent> events)
        => string.Join(
            Environment.NewLine,
            events.Select(item =>
                $"{item.Protocol} {item.EventKind} command={item.Command ?? ""} path={item.RelativePath ?? ""} bytes={item.ByteCount?.ToString() ?? ""} result={item.Result} message={item.Message ?? ""}"));

    private static string DrainEvents(TransferEventBus eventBus)
    {
        var events = new List<TransferEvent>();
        while (eventBus.Reader.TryRead(out var transferEvent))
        {
            events.Add(transferEvent);
        }

        return FormatEvents(events);
    }

    private static async Task<IReadOnlyList<TransferEvent>> DrainEventsAsync(
        TransferEventBus eventBus,
        Func<IReadOnlyList<TransferEvent>, bool> isComplete,
        TimeSpan timeout)
    {
        var events = new List<TransferEvent>();
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline && !isComplete(events))
        {
            while (eventBus.Reader.TryRead(out var transferEvent))
            {
                events.Add(transferEvent);
            }

            if (isComplete(events))
            {
                break;
            }

            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            using var timeoutToken = new CancellationTokenSource(
                remaining < TimeSpan.FromMilliseconds(100)
                    ? remaining
                    : TimeSpan.FromMilliseconds(100));
            try
            {
                await eventBus.Reader.WaitToReadAsync(timeoutToken.Token);
            }
            catch (OperationCanceledException)
            {
            }
        }

        while (eventBus.Reader.TryRead(out var transferEvent))
        {
            events.Add(transferEvent);
        }

        return events;
    }

    private static string FindWorkspaceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AGENTS.md")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find workspace root containing AGENTS.md.");
    }
}
