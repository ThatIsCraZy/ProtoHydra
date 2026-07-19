using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using DataService.Core.Authentication;
using DataService.Core.Events;
using DataService.Protocols.Abstractions;
using DataService.Protocols.Ftp;
using DataService.Protocols.Http;
using DataService.Protocols.Ssh;
using FluentFTP;
using FluentFTP.Exceptions;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace DataService.IntegrationTests;

/// <summary>
/// End-to-end proof that the defined-users policy is enforced by real protocol
/// clients: wrong credentials are rejected, correct credentials still transfer.
/// </summary>
public sealed class AuthenticationEnforcementTests
{
    private static readonly string WorkspaceRoot = FindWorkspaceRoot();

    private static IAuthenticationPolicy CreateDefinedUsers()
        => new DefinedUsersAuthenticationPolicy(
            [new UserAccount("alice", Argon2PasswordHasher.Hash("s3cret"))]);

    [Fact]
    public async Task Http_DefinedUsers_Requires401ThenAcceptsBasicAuth()
    {
        var rootPath = CreateRoot("http-auth");
        await File.WriteAllTextAsync(Path.Combine(rootPath, "file.txt"), "protected-ok");
        var port = GetFreeTcpPort();
        var adapter = new HttpFileServerAdapter(ProtocolKind.Http, new TransferEventBus(), CreateDefinedUsers());

        await adapter.StartAsync(new ProtocolConfiguration("127.0.0.1", port, rootPath, Enabled: true), CancellationToken.None);
        try
        {
            using var client = new HttpClient();

            using var anonymous = await client.GetAsync($"http://127.0.0.1:{port}/file.txt");
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            Assert.Contains("Basic", anonymous.Headers.WwwAuthenticate.ToString());

            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("alice:wrong")));
            using var wrongPassword = await client.GetAsync($"http://127.0.0.1:{port}/file.txt");
            Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);

            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("alice:s3cret")));
            using var authorized = await client.GetAsync($"http://127.0.0.1:{port}/file.txt");
            authorized.EnsureSuccessStatusCode();
            Assert.Equal("protected-ok", await authorized.Content.ReadAsStringAsync());
        }
        finally
        {
            await adapter.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Ftp_DefinedUsers_RejectsWrongLogin_AcceptsCorrectLogin()
    {
        var rootPath = CreateRoot("ftp-auth");
        await File.WriteAllTextAsync(Path.Combine(rootPath, "file.txt"), "ftp-protected-ok");
        var port = GetFreeTcpPort();
        var adapter = new FtpFileServerAdapter(ProtocolKind.Ftp, new TransferEventBus(), CreateDefinedUsers());

        await adapter.StartAsync(new ProtocolConfiguration("127.0.0.1", port, rootPath, Enabled: true), CancellationToken.None);
        try
        {
            await using (var rejected = new AsyncFtpClient("127.0.0.1", "alice", "wrong", port))
            {
                await Assert.ThrowsAsync<FtpAuthenticationException>(() => rejected.Connect());
            }

            await using var accepted = new AsyncFtpClient("127.0.0.1", "alice", "s3cret", port);
            await accepted.Connect();
            var bytes = await accepted.DownloadBytes("file.txt", CancellationToken.None);
            Assert.Equal("ftp-protected-ok", Encoding.UTF8.GetString(bytes!));
        }
        finally
        {
            await adapter.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Sftp_DefinedUsers_RejectsWrongPassword_AcceptsCorrectPassword()
    {
        var rootPath = CreateRoot("sftp-auth");
        await File.WriteAllTextAsync(Path.Combine(rootPath, "file.txt"), "sftp-protected-ok");
        var port = GetFreeTcpPort();
        var hostKeyDirectory = Path.Combine(WorkspaceRoot, "test-artifacts", "ssh");
        using var sharedServer = new SharedSshServer(new TransferEventBus(), hostKeyDirectory, CreateDefinedUsers());
        var adapter = new SftpFileServerAdapter(sharedServer);

        await adapter.StartAsync(new ProtocolConfiguration("127.0.0.1", port, rootPath, Enabled: true), CancellationToken.None);
        try
        {
            using (var rejected = new SftpClient("127.0.0.1", port, "alice", "wrong"))
            {
                Assert.Throws<SshAuthenticationException>(() => rejected.Connect());
            }

            using var accepted = new SftpClient("127.0.0.1", port, "alice", "s3cret");
            accepted.Connect();
            Assert.Equal("sftp-protected-ok", accepted.ReadAllText("/file.txt"));
        }
        finally
        {
            await adapter.StopAsync(CancellationToken.None);
        }
    }

    private static string CreateRoot(string protocol)
    {
        var path = Path.Combine(WorkspaceRoot, "root-Publish-Testfolder", protocol, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static int GetFreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string FindWorkspaceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ProtoHydra.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Workspace root not found.");
    }
}
