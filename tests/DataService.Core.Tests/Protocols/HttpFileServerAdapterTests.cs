using System.Net;
using System.Net.Sockets;
using DataService.Core.Events;
using DataService.Protocols.Abstractions;
using DataService.Protocols.Http;

namespace DataService.Core.Tests.Protocols;

public sealed class HttpFileServerAdapterTests : IDisposable
{
    private readonly HttpClient _client = new();
    private readonly string _rootPath;

    public HttpFileServerAdapterTests()
    {
        _rootPath = Path.Combine(AppContext.BaseDirectory, "test-work", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_rootPath);
    }

    [Fact]
    public async Task Get_ReturnsFileWithRequiredOctetStreamMimeType()
    {
        await File.WriteAllTextAsync(Path.Combine(_rootPath, "boot.iso"), "iso-content");
        var port = GetFreePort();
        var adapter = new HttpFileServerAdapter(ProtocolKind.Http, new TransferEventBus());

        await adapter.StartAsync(CreateConfiguration(port), CancellationToken.None);
        try
        {
            using var response = await _client.GetAsync($"http://127.0.0.1:{port}/boot.iso");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("application/octet-stream", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal("iso-content", await response.Content.ReadAsStringAsync());
        }
        finally
        {
            await adapter.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Range_ReturnsRequestedBytes()
    {
        await File.WriteAllTextAsync(Path.Combine(_rootPath, "file.bin"), "abcdef");
        var port = GetFreePort();
        var adapter = new HttpFileServerAdapter(ProtocolKind.Http, new TransferEventBus());

        await adapter.StartAsync(CreateConfiguration(port), CancellationToken.None);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/file.bin");
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(1, 3);

            using var response = await _client.SendAsync(request);

            Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
            Assert.Equal("bcd", await response.Content.ReadAsStringAsync());
        }
        finally
        {
            await adapter.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Traversal_ReturnsForbidden()
    {
        var port = GetFreePort();
        var adapter = new HttpFileServerAdapter(ProtocolKind.Http, new TransferEventBus());

        await adapter.StartAsync(CreateConfiguration(port), CancellationToken.None);
        try
        {
            using var response = await _client.GetAsync($"http://127.0.0.1:{port}/%252e%252e%252fsecret.txt");

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
        finally
        {
            await adapter.StopAsync(CancellationToken.None);
        }
    }

    public void Dispose()
    {
        _client.Dispose();
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }

    private ProtocolConfiguration CreateConfiguration(int port)
        => new("127.0.0.1", port, _rootPath, Enabled: true);

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
