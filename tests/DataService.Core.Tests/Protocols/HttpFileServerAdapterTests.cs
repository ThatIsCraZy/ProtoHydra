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

    [Theory]
    [InlineData("/folder")]
    [InlineData("/folder/")]
    [InlineData("/folder%20name")]
    public async Task DirectoryListing_FileLinkResolvesInsideRequestedDirectory(string path)
    {
        var folder = Uri.UnescapeDataString(path.Trim('/'));
        Directory.CreateDirectory(Path.Combine(_rootPath, folder));
        await File.WriteAllTextAsync(Path.Combine(_rootPath, folder, "file.txt"), "nested-content");
        var port = GetFreePort();
        var adapter = new HttpFileServerAdapter(ProtocolKind.Http, new TransferEventBus());
        await adapter.StartAsync(CreateConfiguration(port), CancellationToken.None);
        try
        {
            using var listing = await _client.GetAsync($"http://127.0.0.1:{port}{path}");
            listing.EnsureSuccessStatusCode();
            var html = await listing.Content.ReadAsStringAsync();
            var match = System.Text.RegularExpressions.Regex.Match(html, "href=\"([^\"]+)\">file.txt</a>");
            Assert.True(match.Success, html);
            var fileUri = new Uri(listing.RequestMessage!.RequestUri!, WebUtility.HtmlDecode(match.Groups[1].Value));

            using var response = await _client.GetAsync(fileUri);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("nested-content", await response.Content.ReadAsStringAsync());
        }
        finally
        {
            await adapter.StopAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData("./file.txt")]
    [InlineData("./folder/./file.txt")]
    [InlineData("/./folder/./file.txt")]
    public async Task DotPaths_DownloadFiles(string path)
    {
        Directory.CreateDirectory(Path.Combine(_rootPath, "folder"));
        await File.WriteAllTextAsync(Path.Combine(_rootPath, "file.txt"), "dot-content");
        await File.WriteAllTextAsync(Path.Combine(_rootPath, "folder", "file.txt"), "dot-content");
        var port = GetFreePort();
        var adapter = new HttpFileServerAdapter(ProtocolKind.Http, new TransferEventBus());
        await adapter.StartAsync(CreateConfiguration(port), CancellationToken.None);
        try
        {
            // Keep dot segments on the wire instead of letting System.Uri remove them.
            var uri = new Uri($"http://127.0.0.1:{port}/{path.TrimStart('/')}",
                new UriCreationOptions { DangerousDisablePathAndQueryCanonicalization = true });
            using var response = await _client.GetAsync(uri);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("dot-content", await response.Content.ReadAsStringAsync());
        }
        finally
        {
            await adapter.StopAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData("bytes=6-", "abcdef")]
    [InlineData("bytes=-0", "abcdef")]
    [InlineData("bytes=0-", "")]
    public async Task Range_Unsatisfiable_Returns416(string range, string content)
    {
        await File.WriteAllTextAsync(Path.Combine(_rootPath, "file.bin"), content);
        var port = GetFreePort();
        var bus = new TransferEventBus();
        var adapter = new HttpFileServerAdapter(ProtocolKind.Http, bus);
        await adapter.StartAsync(CreateConfiguration(port), CancellationToken.None);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/file.bin");
            request.Headers.TryAddWithoutValidation("Range", range);
            using var response = await _client.SendAsync(request);
            Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, response.StatusCode);
            Assert.Equal($"bytes */{content.Length}", response.Content.Headers.ContentRange?.ToString());
            Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        }
        finally
        {
            await adapter.StopAsync(CancellationToken.None);
        }

        var events = new List<TransferEvent>();
        while (bus.Reader.TryRead(out var item))
        {
            events.Add(item);
        }
        Assert.Contains(events, item => item.EventKind == TransferEventKind.RequestRejected
            && item.Result == TransferResult.Rejected && item.ByteCount == 0);
    }

    [Theory]
    [InlineData("bytes=2-", "cdef", "bytes 2-5/6")]
    [InlineData("bytes=-2", "ef", "bytes 4-5/6")]
    [InlineData("bytes=2-99", "cdef", "bytes 2-5/6")]
    [InlineData("bytes=-99", "abcdef", "bytes 0-5/6")]
    [InlineData("bytes=0-9223372036854775807", "abcdef", "bytes 0-5/6")]
    public async Task Range_OpenEndedSuffixAndOversizedEnd_ReturnRequestedBytes(
        string range, string expectedBody, string expectedContentRange)
    {
        await File.WriteAllTextAsync(Path.Combine(_rootPath, "file.bin"), "abcdef");
        var port = GetFreePort();
        var adapter = new HttpFileServerAdapter(ProtocolKind.Http, new TransferEventBus());
        await adapter.StartAsync(CreateConfiguration(port), CancellationToken.None);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/file.bin");
            request.Headers.TryAddWithoutValidation("Range", range);
            using var response = await _client.SendAsync(request);

            Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
            Assert.Equal(expectedContentRange, response.Content.Headers.ContentRange?.ToString());
            Assert.Equal(expectedBody, await response.Content.ReadAsStringAsync());
        }
        finally
        {
            await adapter.StopAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData("bytes=oops", null)]
    [InlineData("bytes=0-1,4-5", null)]
    [InlineData("items=1-2", null)]
    [InlineData("bytes=2-3", "\"old-version\"")]
    public async Task Range_UnsupportedOrConditional_ReturnsFullFile(string range, string? ifRange)
    {
        await File.WriteAllTextAsync(Path.Combine(_rootPath, "file.bin"), "abcdef");
        var port = GetFreePort();
        var adapter = new HttpFileServerAdapter(ProtocolKind.Http, new TransferEventBus());
        await adapter.StartAsync(CreateConfiguration(port), CancellationToken.None);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/file.bin");
            request.Headers.TryAddWithoutValidation("Range", range);
            if (ifRange is not null)
            {
                request.Headers.TryAddWithoutValidation("If-Range", ifRange);
            }
            using var response = await _client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Null(response.Content.Headers.ContentRange);
            Assert.Equal("abcdef", await response.Content.ReadAsStringAsync());
        }
        finally
        {
            await adapter.StopAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData("/file.bin")]
    [InlineData("/")]
    public async Task Head_ReportsGetContentLengthWithoutTransferringBody(string path)
    {
        await File.WriteAllTextAsync(Path.Combine(_rootPath, "file.bin"), "abcdef");
        var port = GetFreePort();
        var bus = new TransferEventBus();
        var adapter = new HttpFileServerAdapter(ProtocolKind.Http, bus);
        await adapter.StartAsync(CreateConfiguration(port), CancellationToken.None);
        try
        {
            var url = $"http://127.0.0.1:{port}{path}";
            using var get = await _client.GetAsync(url);
            var content = await get.Content.ReadAsByteArrayAsync();
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(1, 2);
            using var response = await _client.SendAsync(request);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(content.LongLength, response.Content.Headers.ContentLength);
            Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        }
        finally
        {
            await adapter.StopAsync(CancellationToken.None);
        }

        var events = new List<TransferEvent>();
        while (bus.Reader.TryRead(out var item))
        {
            events.Add(item);
        }
        Assert.Contains(events, item => item.Command == "HEAD" && item.ByteCount == 0);
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
