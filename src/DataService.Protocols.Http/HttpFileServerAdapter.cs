using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using DataService.Core.Authentication;
using DataService.Core.Diagnostics;
using DataService.Core.Events;
using DataService.Core.FileSystem;
using DataService.Infrastructure.Certificates;
using DataService.Protocols.Abstractions;
using WatsonWebserver;
using WatsonWebserver.Core;
using WatsonWebserver.Core.Settings;
using WHttpMethod = WatsonWebserver.Core.HttpMethod;

namespace DataService.Protocols.Http;

public sealed class HttpFileServerAdapter : IProtocolAdapter
{
    private readonly ITransferEventBus _eventBus;
    private readonly IAuthenticationPolicy _authenticationPolicy;
    private readonly ICertificateManager? _certificateManager;
    private readonly CertificateSettings? _certificateSettings;
    private readonly bool _useTls;
    private Webserver? _server;
    private CancellationTokenSource? _serverTokenSource;
    private Task? _serverTask;
    private IPEndPoint? _boundEndpoint;

    public HttpFileServerAdapter(
        ProtocolKind protocol,
        ITransferEventBus eventBus,
        IAuthenticationPolicy? authenticationPolicy = null,
        ICertificateManager? certificateManager = null,
        CertificateSettings? certificateSettings = null)
    {
        if (protocol is not (ProtocolKind.Http or ProtocolKind.Https))
        {
            throw new ArgumentException("Only HTTP and HTTPS are supported.", nameof(protocol));
        }

        Protocol = protocol;
        _eventBus = eventBus;
        _authenticationPolicy = authenticationPolicy ?? new AcceptAnyAuthenticationPolicy();
        _certificateManager = certificateManager;
        _certificateSettings = certificateSettings;
        _useTls = protocol == ProtocolKind.Https;
        Capabilities = new ProtocolCapabilities(
            SupportsDownload: true,
            SupportsUpload: false,
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

        State = ProtocolRuntimeState.Starting;
        Publish(TransferEventKind.ListenerStarting, configuration, TransferResult.Success);

        try
        {
            var settings = new WebserverSettings(configuration.BindAddress, configuration.Port, _useTls);
            if (_useTls)
            {
                if (_certificateManager is null || _certificateSettings is null)
                {
                    throw new InvalidOperationException("HTTPS requires a certificate manager and certificate settings.");
                }

                settings.Ssl = new SslSettings
                {
                    Enable = true,
                    SslCertificate = await _certificateManager.GetOrCreateAsync(
                        CertificatePurpose.Https,
                        _certificateSettings,
                        cancellationToken)
                };
            }

            var resolver = new RootPathResolver(configuration.RootPath);
            _server = new Webserver(settings, context => HandleRequestAsync(context, resolver));
            _serverTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _serverTask = Task.Run(() => _server.Start(_serverTokenSource.Token), CancellationToken.None);
            await WaitUntilListeningAsync(_server, _serverTask, cancellationToken);
            _boundEndpoint = IPAddress.TryParse(configuration.BindAddress, out var boundAddress)
                ? new IPEndPoint(boundAddress, configuration.Port)
                : null;
            State = ProtocolRuntimeState.Running;
            Publish(TransferEventKind.ListenerStarted, configuration, TransferResult.Success);
        }
        catch
        {
            State = ProtocolRuntimeState.Faulted;
            Publish(TransferEventKind.ListenerFaulted, configuration, TransferResult.Failed);
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

        // Order matters: cancelling the token that was handed to Webserver.Start first
        // aborts Watson's own teardown and leaks its TcpListener for the lifetime of the
        // process, so the port can never be reused. Stop/Dispose release it; the token
        // source is only cleaned up afterwards.
        try
        {
            _server?.Stop();
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("already stopped", StringComparison.OrdinalIgnoreCase))
        {
        }

        _server?.Dispose();
        if (_serverTask is not null)
        {
            await Task.WhenAny(_serverTask, Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None));
        }

        _serverTokenSource?.Dispose();
        _serverTokenSource = null;
        _serverTask = null;
        _server = null;

        // The listening socket outlives Stop()/Dispose() by a moment. Returning early
        // makes an immediate restart on the same port (root folder change) fail
        // validation with "Port is not available".
        if (_boundEndpoint is not null)
        {
            await WaitUntilPortReleasedAsync(_boundEndpoint, cancellationToken);
            _boundEndpoint = null;
        }

        State = ProtocolRuntimeState.Stopped;
    }

    private static async Task WaitUntilPortReleasedAsync(
        IPEndPoint endpoint,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            try
            {
                using var probe = new TcpListener(endpoint.Address, endpoint.Port);
                probe.Start();
                probe.Stop();
                return;
            }
            catch (SocketException)
            {
                if (Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(5))
                {
                    // Give up quietly; the next start reports the port conflict itself.
                    return;
                }

                await Task.Delay(25, cancellationToken);
            }
        }
    }

    private static async Task WaitUntilListeningAsync(
        Webserver server,
        Task serverTask,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        while (!server.IsListening)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (serverTask.IsFaulted)
            {
                await serverTask;
            }

            if (Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(5))
            {
                throw new TimeoutException("HTTP listener did not start within 5 seconds.");
            }

            await Task.Delay(10, cancellationToken);
        }
    }

    private async Task HandleRequestAsync(HttpContextBase context, RootPathResolver resolver)
    {
        var started = Stopwatch.GetTimestamp();
        var method = context.Request.Method;
        var path = context.Request.Url.RawWithoutQuery;
        var byteCount = 0L;
        var result = TransferResult.Success;
        string? relativePath = null;
        string? fullPath = null;
        string? failureMessage = null;
        IoErrorCategory? ioError = null;

        try
        {
            if (_authenticationPolicy.RequiresCredentials && !IsAuthorized(context))
            {
                context.Response.StatusCode = 401;
                context.Response.Headers.Add("WWW-Authenticate", "Basic realm=\"ProtoHydra\", charset=\"UTF-8\"");
                result = TransferResult.Rejected;
                failureMessage = "Authentication required.";
                await context.Response.Send("Unauthorized", context.Token);
                return;
            }

            if (method is not (WHttpMethod.GET or WHttpMethod.HEAD))
            {
                context.Response.StatusCode = 405;
                context.Response.Headers.Add("Allow", "GET, HEAD");
                result = TransferResult.Rejected;
                await context.Response.Send("Method Not Allowed", context.Token);
                return;
            }

            ResolvedPath resolved;
            try
            {
                resolved = resolver.ResolveClientPath(path);
                relativePath = resolved.RelativePath;
                fullPath = resolved.FullPath;
            }
            catch (PathResolutionException)
            {
                context.Response.StatusCode = 403;
                result = TransferResult.Rejected;
                await context.Response.Send("Forbidden", context.Token);
                return;
            }

            if (Directory.Exists(resolved.FullPath))
            {
                var html = BuildDirectoryListing(resolved);
                context.Response.ContentType = "text/html; charset=utf-8";
                byteCount = Encoding.UTF8.GetByteCount(html);
                if (method == WHttpMethod.HEAD)
                {
                    await context.Response.Send(byteCount, context.Token);
                    byteCount = 0;
                    return;
                }

                await context.Response.Send(html, context.Token);
                return;
            }

            if (!File.Exists(resolved.FullPath))
            {
                context.Response.StatusCode = 404;
                result = TransferResult.Failed;
                await context.Response.Send("Not Found", context.Token);
                return;
            }

            byteCount = await SendFileAsync(context, resolved.FullPath, method, context.Token);
            if (context.Response.StatusCode == 416)
            {
                result = TransferResult.Rejected;
                failureMessage = "Requested byte range is not satisfiable.";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            result = TransferResult.Failed;
            byteCount = 0;
            ioError = IoErrorClassifier.Classify(ex, fullPath);
            failureMessage = IoErrorClassifier.BuildMessage(ex, fullPath);
            try
            {
                context.Response.StatusCode = 500;
                await context.Response.Send("Internal Server Error", context.Token);
            }
            catch (Exception)
            {
                // Response already started; the connection ends with the broken transfer.
            }
        }
        finally
        {
            var eventKind = result switch
            {
                TransferResult.Rejected => TransferEventKind.RequestRejected,
                TransferResult.Failed => TransferEventKind.DownloadFailed,
                _ when method == WHttpMethod.HEAD => TransferEventKind.CommandReceived,
                _ => TransferEventKind.DownloadCompleted
            };
            var statusCode = context.Response.StatusCode.ToString(CultureInfo.InvariantCulture);
            _eventBus.TryPublish(new TransferEvent(
                DateTimeOffset.UtcNow,
                Protocol,
                eventKind,
                TryParseAddress(context.Request.Source.IpAddress),
                TryGetBasicUsername(context.Request.Authorization),
                method.ToString(),
                relativePath,
                TransferDirection.Download,
                byteCount,
                Stopwatch.GetElapsedTime(started),
                result,
                failureMessage is null ? statusCode : $"{statusCode} {failureMessage}",
                context.Guid.ToString("N"),
                ioError));
        }
    }

    private static async Task<long> SendFileAsync(
        HttpContextBase context,
        string fullPath,
        WHttpMethod method,
        CancellationToken cancellationToken)
    {
        // Open before sending any headers so permission/stub/device failures can
        // still be answered with a clean HTTP error status.
        var file = new FileInfo(fullPath);
        await using var stream = method == WHttpMethod.HEAD ? null : new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var fileLength = stream?.Length ?? file.Length;

        // Range only applies to GET. Without validator support, conservatively
        // send the full representation for If-Range instead of mixing versions.
        var rangeHeader = method == WHttpMethod.GET && context.Request.Headers["If-Range"] is null
            ? context.Request.Headers["Range"]
            : null;
        var range = ResolveRange(rangeHeader, fileLength);

        context.Response.ContentType = ResolveMimeType(file.Extension);
        context.Response.Headers.Add("Accept-Ranges", "bytes");
        context.Response.Headers.Add("Last-Modified", file.LastWriteTimeUtc.ToString("R", CultureInfo.InvariantCulture));
        if (range is null)
        {
            context.Response.StatusCode = 416;
            context.Response.Headers.Add("Content-Range", $"bytes */{fileLength}");
            await context.Response.Send(0, cancellationToken);
            return 0;
        }

        var contentLength = range.Length;

        if (range.IsPartial)
        {
            context.Response.StatusCode = 206;
            context.Response.Headers.Add(
                "Content-Range",
                $"bytes {range.Start}-{range.End}/{fileLength}");
        }

        if (method == WHttpMethod.HEAD)
        {
            await context.Response.Send(contentLength, cancellationToken);
            return 0;
        }

        stream!.Position = range.Start;
        await context.Response.Send(contentLength, stream, cancellationToken);

        return contentLength;
    }

    private static string BuildDirectoryListing(ResolvedPath resolved)
    {
        var builder = new StringBuilder();
        // Root-relative links also work when the request URL has no trailing '/'.
        var directoryUrl = DirectoryUrl(resolved.RelativePath);
        builder.AppendLine("<!doctype html><html><head><meta charset=\"utf-8\"><title>Index</title></head><body>");
        builder.AppendLine("<h1>Index</h1><table><tr><th>Name</th><th>Size</th><th>Modified</th></tr>");

        if (!string.IsNullOrEmpty(resolved.RelativePath))
        {
            var separator = resolved.RelativePath.LastIndexOf('/');
            var parent = separator < 0 ? string.Empty : resolved.RelativePath[..separator];
            builder.Append("<tr><td><a href=\"").Append(DirectoryUrl(parent))
                .AppendLine("\">..</a></td><td></td><td></td></tr>");
        }

        foreach (var directory in Directory.EnumerateDirectories(resolved.FullPath).Order(StringComparer.OrdinalIgnoreCase))
        {
            // One unreadable entry must not break the whole listing.
            try
            {
                var info = new DirectoryInfo(directory);
                var href = directoryUrl + Uri.EscapeDataString(info.Name) + "/";
                builder.Append("<tr><td><a href=\"").Append(href).Append("\">")
                    .Append(WebUtility.HtmlEncode(info.Name)).Append("/</a></td><td></td><td>")
                    .Append(info.LastWriteTimeUtc.ToString("u", CultureInfo.InvariantCulture))
                    .AppendLine("</td></tr>");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        foreach (var file in Directory.EnumerateFiles(resolved.FullPath).Order(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var info = new FileInfo(file);
                var href = directoryUrl + Uri.EscapeDataString(info.Name);
                builder.Append("<tr><td><a href=\"").Append(href).Append("\">")
                    .Append(WebUtility.HtmlEncode(info.Name)).Append("</a></td><td>")
                    .Append(info.Length.ToString(CultureInfo.InvariantCulture)).Append("</td><td>")
                    .Append(info.LastWriteTimeUtc.ToString("u", CultureInfo.InvariantCulture))
                    .AppendLine("</td></tr>");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        builder.AppendLine("</table></body></html>");
        return builder.ToString();
    }

    private static string DirectoryUrl(string relativePath)
        => relativePath.Length == 0
            ? "/"
            : "/" + string.Join('/', relativePath.Split('/').Select(Uri.EscapeDataString)) + "/";

    private static string ResolveMimeType(string extension)
        => extension.ToLowerInvariant() switch
        {
            ".html" or ".htm" => "text/html; charset=utf-8",
            ".txt" or ".log" => "text/plain; charset=utf-8",
            ".json" => "application/json",
            ".css" => "text/css",
            ".js" => "text/javascript",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".svg" => "image/svg+xml",
            ".iso" or ".img" => "application/octet-stream",
            _ => "application/octet-stream"
        };

    private static ByteRange? ResolveRange(string? rangeHeader, long fileLength)
    {
        // Use the framework's HTTP header parser. Multipart ranges are optional;
        // ignore them (and malformed headers) and serve the full file.
        if (!RangeHeaderValue.TryParse(rangeHeader, out var header)
            || !string.Equals(header.Unit, "bytes", StringComparison.OrdinalIgnoreCase)
            || header.Ranges.Count != 1)
        {
            return new ByteRange(0, Math.Max(0, fileLength - 1), fileLength, IsPartial: false);
        }

        var requested = header.Ranges.Single();
        if (fileLength == 0 || requested.From >= fileLength
            || (requested.From is null && requested.To == 0))
        {
            return null;
        }

        var start = requested.From ?? Math.Max(0, fileLength - requested.To!.Value);
        var end = requested.From is null ? fileLength - 1 : Math.Min(requested.To ?? fileLength - 1, fileLength - 1);
        return new ByteRange(start, end, end - start + 1, IsPartial: true);
    }

    private static IPAddress? TryParseAddress(string? value)
        => IPAddress.TryParse(value, out var address) ? address : null;

    private static string? TryGetBasicUsername(AuthorizationDetails authorization)
        => string.IsNullOrWhiteSpace(authorization.Username) ? null : authorization.Username;

    private bool IsAuthorized(HttpContextBase context)
    {
        var authorization = context.Request.Authorization;
        return _authenticationPolicy
            .AuthenticatePassword(authorization.Username, authorization.Password)
            .Accepted;
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

    private sealed record ByteRange(long Start, long End, long Length, bool IsPartial);
}
