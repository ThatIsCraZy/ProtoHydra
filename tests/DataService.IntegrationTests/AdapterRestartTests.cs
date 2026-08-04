using System.Net;
using System.Net.Sockets;
using DataService.Core.Events;
using DataService.Protocols.Abstractions;
using DataService.Protocols.Ftp;
using DataService.Protocols.Http;
using DataService.Protocols.Ssh;
using DataService.Protocols.Tftp;

namespace DataService.IntegrationTests;

/// <summary>
/// Restarting a listener immediately (as a root-folder change does) must succeed:
/// StopAsync may not return while the listening socket is still bound, otherwise the
/// following StartAsync fails validation with "Port is not available".
/// </summary>
public sealed class AdapterRestartTests
{
    [Fact]
    public async Task Http_StopThenImmediateStart_SucceedsOnSamePort()
    {
        var firstRoot = CreateRoot("restart-a");
        var secondRoot = CreateRoot("restart-b");
        await File.WriteAllTextAsync(Path.Combine(firstRoot, "a.txt"), "first-root");
        await File.WriteAllTextAsync(Path.Combine(secondRoot, "b.txt"), "second-root");
        var port = GetFreeTcpPort();
        var adapter = new HttpFileServerAdapter(ProtocolKind.Http, new TransferEventBus());

        await adapter.StartAsync(
            new ProtocolConfiguration("127.0.0.1", port, firstRoot, Enabled: true),
            CancellationToken.None);
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            Assert.Equal("first-root", await client.GetStringAsync($"http://127.0.0.1:{port}/a.txt"));

            // No delay between stop and start — this is what the root-folder restart does.
            await adapter.StopAsync(CancellationToken.None);

            var configuration = new ProtocolConfiguration("127.0.0.1", port, secondRoot, Enabled: true);
            var validation = await adapter.ValidateAsync(configuration, CancellationToken.None);
            Assert.True(validation.IsValid, $"Port was still bound after StopAsync: {validation.Message}");

            await adapter.StartAsync(configuration, CancellationToken.None);
            Assert.Equal(ProtocolRuntimeState.Running, adapter.State);
            Assert.Equal("second-root", await client.GetStringAsync($"http://127.0.0.1:{port}/b.txt"));
        }
        finally
        {
            await adapter.StopAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData("ftp")]
    [InlineData("tftp")]
    [InlineData("sftp")]
    public async Task Adapter_StopThenImmediateStart_SucceedsOnSamePort(string protocol)
    {
        var root = CreateRoot($"restart-{protocol}");
        var eventBus = new TransferEventBus();
        var hostKeyDirectory = Path.Combine(FindWorkspaceRoot(), "test-artifacts", "ssh");
        var (adapter, port) = protocol switch
        {
            "ftp" => ((IProtocolAdapter)new FtpFileServerAdapter(ProtocolKind.Ftp, eventBus), GetFreeTcpPort()),
            "tftp" => (new TftpFileServerAdapter(eventBus), GetFreeUdpPort()),
            _ => (new SftpFileServerAdapter(eventBus, hostKeyDirectory), GetFreeTcpPort())
        };

        var configuration = new ProtocolConfiguration("127.0.0.1", port, root, Enabled: true);
        await adapter.StartAsync(configuration, CancellationToken.None);
        try
        {
            Assert.Equal(ProtocolRuntimeState.Running, adapter.State);

            await adapter.StopAsync(CancellationToken.None);

            var validation = await adapter.ValidateAsync(configuration, CancellationToken.None);
            Assert.True(validation.IsValid, $"{protocol}: port still bound after StopAsync: {validation.Message}");

            await adapter.StartAsync(configuration, CancellationToken.None);
            Assert.Equal(ProtocolRuntimeState.Running, adapter.State);
        }
        finally
        {
            await adapter.StopAsync(CancellationToken.None);
        }
    }

    private static int GetFreeUdpPort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    private static string CreateRoot(string name)
    {
        var path = Path.Combine(
            FindWorkspaceRoot(),
            "root-Publish-Testfolder",
            name,
            Guid.NewGuid().ToString("N"));
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

        return directory?.FullName ?? throw new InvalidOperationException("Workspace root not found.");
    }
}
