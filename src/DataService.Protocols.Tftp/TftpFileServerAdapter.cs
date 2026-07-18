using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using DataService.Core.Diagnostics;
using DataService.Core.Events;
using DataService.Core.FileSystem;
using DataService.Protocols.Abstractions;
using Tftp.Net;

namespace DataService.Protocols.Tftp;

public sealed class TftpFileServerAdapter : IProtocolAdapter
{
    private const ushort ErrorFileNotFound = 1;
    private const ushort ErrorAccessViolation = 2;
    private const ushort ErrorIllegalOperation = 4;

    private readonly ConcurrentDictionary<ITftpTransfer, ActiveTransfer> _activeTransfers = new();
    private readonly ITransferEventBus _eventBus;
    private TftpServer? _server;
    private RootPathResolver? _resolver;

    public TftpFileServerAdapter(ITransferEventBus eventBus)
    {
        _eventBus = eventBus;
        Capabilities = new ProtocolCapabilities(
            SupportsDownload: true,
            SupportsUpload: true,
            SupportsListing: false,
            SupportsAuthentication: false,
            UsesEncryption: false);
    }

    public ProtocolKind Protocol => ProtocolKind.Tftp;

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
            return Task.FromResult(ProtocolValidationResult.Failure("Port is outside the valid UDP port range."));
        }

        try
        {
            using var listener = new UdpClient(new IPEndPoint(address, configuration.Port));
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
        PublishListener(TransferEventKind.ListenerStarting, configuration, TransferResult.Success);

        try
        {
            var address = IPAddress.Parse(configuration.BindAddress);
            _resolver = new RootPathResolver(configuration.RootPath);
            _server = new TftpServer(address, configuration.Port);
            _server.OnReadRequest += HandleReadRequest;
            _server.OnWriteRequest += HandleWriteRequest;
            _server.OnError += HandleServerError;
            _server.Start();

            State = ProtocolRuntimeState.Running;
            PublishListener(TransferEventKind.ListenerStarted, configuration, TransferResult.Success);
        }
        catch
        {
            State = ProtocolRuntimeState.Faulted;
            PublishListener(TransferEventKind.ListenerFaulted, configuration, TransferResult.Failed);
            DisposeServer();
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (State is ProtocolRuntimeState.Stopped or ProtocolRuntimeState.Stopping)
        {
            return Task.CompletedTask;
        }

        State = ProtocolRuntimeState.Stopping;

        foreach (var transfer in _activeTransfers.Keys)
        {
            try
            {
                transfer.Cancel(new TftpErrorPacket(ErrorIllegalOperation, "Server is stopping."));
            }
            catch (InvalidOperationException)
            {
            }
        }

        DisposeServer();
        foreach (var context in _activeTransfers.Values)
        {
            context.Dispose(deleteTemporaryUpload: true);
        }

        _activeTransfers.Clear();
        _resolver = null;
        State = ProtocolRuntimeState.Stopped;
        return Task.CompletedTask;
    }

    private void HandleReadRequest(ITftpTransfer transfer, EndPoint client)
    {
        var context = CreateContext(transfer, client, TransferDirection.Download, "RRQ");
        if (!TryPrepareTransfer(transfer, context, out var resolved))
        {
            return;
        }

        if (!File.Exists(resolved.FullPath) || Directory.Exists(resolved.FullPath))
        {
            Reject(transfer, context, ErrorFileNotFound, "File not found.", TransferEventKind.DownloadFailed);
            return;
        }

        try
        {
            var stream = new FileStream(
                resolved.FullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            context.SetStream(stream);
            context.ExpectedBytes = stream.Length;
            StartTrackedTransfer(transfer, context);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RejectIo(transfer, context, ex, resolved.FullPath, TransferEventKind.DownloadFailed);
        }
    }

    private void HandleWriteRequest(ITftpTransfer transfer, EndPoint client)
    {
        var context = CreateContext(transfer, client, TransferDirection.Upload, "WRQ");
        if (!TryPrepareTransfer(transfer, context, out var resolved))
        {
            return;
        }

        var parent = Path.GetDirectoryName(resolved.FullPath);
        if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent))
        {
            Reject(transfer, context, ErrorFileNotFound, "Target folder does not exist.", TransferEventKind.UploadFailed);
            return;
        }

        try
        {
            var tempPath = Path.Combine(
                parent,
                $".{Path.GetFileName(resolved.FullPath)}.{Guid.NewGuid():N}.uploading");
            var stream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            context.SetStream(stream);
            context.TemporaryUploadPath = tempPath;
            context.FinalUploadPath = resolved.FullPath;
            StartTrackedTransfer(transfer, context);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RejectIo(transfer, context, ex, resolved.FullPath, TransferEventKind.UploadFailed);
        }
    }

    private ActiveTransfer CreateContext(
        ITftpTransfer transfer,
        EndPoint client,
        TransferDirection direction,
        string command)
        => new(
            Stopwatch.GetTimestamp(),
            transfer.Filename,
            GetClientAddress(client),
            command,
            direction);

    private bool TryPrepareTransfer(
        ITftpTransfer transfer,
        ActiveTransfer context,
        out ResolvedPath resolved)
    {
        resolved = null!;
        if (_resolver is null)
        {
            Reject(transfer, context, ErrorIllegalOperation, "Server is not ready.", FailureKind(context.Direction));
            return false;
        }

        try
        {
            resolved = _resolver.ResolveClientPath(transfer.Filename);
            context.RelativePath = resolved.RelativePath;
        }
        catch (PathResolutionException ex)
        {
            Reject(transfer, context, ErrorAccessViolation, ex.Message, TransferEventKind.RequestRejected);
            return false;
        }

        PublishTransfer(StartKind(context.Direction), context, TransferResult.Success, MessageFor(transfer));
        return true;
    }

    private void StartTrackedTransfer(ITftpTransfer transfer, ActiveTransfer context)
    {
        transfer.OnProgress += HandleProgress;
        transfer.OnFinished += HandleFinished;
        transfer.OnError += HandleTransferError;
        _activeTransfers[transfer] = context;
        transfer.Start(context.Stream);
    }

    private void HandleProgress(ITftpTransfer transfer, TftpTransferProgress progress)
    {
        if (_activeTransfers.TryGetValue(transfer, out var context))
        {
            context.TransferredBytes = progress.TransferredBytes;
            if (progress.TotalBytes > 0)
            {
                context.ExpectedBytes = progress.TotalBytes;
            }
        }
    }

    private void HandleFinished(ITftpTransfer transfer)
    {
        if (!_activeTransfers.TryRemove(transfer, out var context))
        {
            return;
        }

        var result = TransferResult.Success;
        var message = "Completed.";
        IoErrorCategory? ioError = null;

        try
        {
            if (context.Direction == TransferDirection.Upload)
            {
                context.Dispose(deleteTemporaryUpload: false);
                if (context.TemporaryUploadPath is null || context.FinalUploadPath is null)
                {
                    throw new InvalidOperationException("Upload target was not initialized.");
                }

                File.Move(context.TemporaryUploadPath, context.FinalUploadPath, overwrite: true);
                context.TransferredBytes = new FileInfo(context.FinalUploadPath).Length;
            }
            else
            {
                context.Dispose(deleteTemporaryUpload: false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            result = TransferResult.Failed;
            message = IoErrorClassifier.BuildMessage(ex, context.FinalUploadPath);
            ioError = IoErrorClassifier.Classify(ex, context.FinalUploadPath);
            context.Dispose(deleteTemporaryUpload: true);
        }

        PublishTransfer(
            result == TransferResult.Failed ? FailureKind(context.Direction) : CompletedKind(context.Direction),
            context,
            result,
            message,
            ioError);
    }

    private void HandleTransferError(ITftpTransfer transfer, TftpTransferError error)
    {
        if (!_activeTransfers.TryRemove(transfer, out var context))
        {
            return;
        }

        context.Dispose(deleteTemporaryUpload: true);
        PublishTransfer(FailureKind(context.Direction), context, TransferResult.Failed, error.ToString());
    }

    private void HandleServerError(TftpTransferError error)
        => _eventBus.TryPublish(new TransferEvent(
            DateTimeOffset.UtcNow,
            Protocol,
            TransferEventKind.ListenerFaulted,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            TransferResult.Failed,
            error.ToString(),
            null));

    private void Reject(
        ITftpTransfer transfer,
        ActiveTransfer context,
        ushort errorCode,
        string message,
        TransferEventKind eventKind)
    {
        try
        {
            transfer.Cancel(new TftpErrorPacket(errorCode, message));
        }
        finally
        {
            context.Dispose(deleteTemporaryUpload: true);
            PublishTransfer(eventKind, context, TransferResult.Rejected, message);
        }
    }

    private void RejectIo(
        ITftpTransfer transfer,
        ActiveTransfer context,
        Exception exception,
        string? fullPath,
        TransferEventKind eventKind)
    {
        var message = IoErrorClassifier.BuildMessage(exception, fullPath);
        try
        {
            transfer.Cancel(new TftpErrorPacket(ErrorAccessViolation, message));
        }
        finally
        {
            context.Dispose(deleteTemporaryUpload: true);
            PublishTransfer(eventKind, context, TransferResult.Failed, message, IoErrorClassifier.Classify(exception, fullPath));
        }
    }

    private void DisposeServer()
    {
        if (_server is not null)
        {
            _server.OnReadRequest -= HandleReadRequest;
            _server.OnWriteRequest -= HandleWriteRequest;
            _server.OnError -= HandleServerError;
            _server.Dispose();
            _server = null;
        }
    }

    private void PublishListener(
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
            $"{configuration.BindAddress}:{configuration.Port.ToString(CultureInfo.InvariantCulture)}",
            null));

    private void PublishTransfer(
        TransferEventKind kind,
        ActiveTransfer context,
        TransferResult result,
        string? message,
        IoErrorCategory? ioError = null)
        => _eventBus.TryPublish(new TransferEvent(
            DateTimeOffset.UtcNow,
            Protocol,
            kind,
            context.SourceAddress,
            null,
            context.Command,
            context.RelativePath ?? context.RequestedPath,
            context.Direction,
            context.TransferredBytes > 0 ? context.TransferredBytes : context.ExpectedBytes,
            Stopwatch.GetElapsedTime(context.StartedAt),
            result,
            message,
            context.CorrelationId,
            ioError));

    private static IPAddress? GetClientAddress(EndPoint endpoint)
        => endpoint is IPEndPoint ipEndPoint ? ipEndPoint.Address : null;

    private static string MessageFor(ITftpTransfer transfer)
        => $"Mode={transfer.TransferMode}; BlockSize={transfer.BlockSize.ToString(CultureInfo.InvariantCulture)}; ExpectedSize={transfer.ExpectedSize.ToString(CultureInfo.InvariantCulture)}";

    private static TransferEventKind StartKind(TransferDirection direction)
        => direction == TransferDirection.Download ? TransferEventKind.DownloadStarted : TransferEventKind.UploadStarted;

    private static TransferEventKind CompletedKind(TransferDirection direction)
        => direction == TransferDirection.Download ? TransferEventKind.DownloadCompleted : TransferEventKind.UploadCompleted;

    private static TransferEventKind FailureKind(TransferDirection direction)
        => direction == TransferDirection.Download ? TransferEventKind.DownloadFailed : TransferEventKind.UploadFailed;

    private sealed class ActiveTransfer : IDisposable
    {
        public ActiveTransfer(
            long startedAt,
            string requestedPath,
            IPAddress? sourceAddress,
            string command,
            TransferDirection direction)
        {
            StartedAt = startedAt;
            RequestedPath = requestedPath;
            SourceAddress = sourceAddress;
            Command = command;
            Direction = direction;
        }

        public long StartedAt { get; }

        public string RequestedPath { get; }

        public IPAddress? SourceAddress { get; }

        public string Command { get; }

        public TransferDirection Direction { get; }

        public string CorrelationId { get; } = Guid.NewGuid().ToString("N");

        public string? RelativePath { get; set; }

        public long? ExpectedBytes { get; set; }

        public long TransferredBytes { get; set; }

        public string? TemporaryUploadPath { get; set; }

        public string? FinalUploadPath { get; set; }

        public Stream Stream { get; private set; } = Stream.Null;

        public void SetStream(Stream stream)
            => Stream = stream;

        public void Dispose()
            => Dispose(deleteTemporaryUpload: false);

        public void Dispose(bool deleteTemporaryUpload)
        {
            try
            {
                Stream.Dispose();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }

            try
            {
                if (deleteTemporaryUpload
                    && TemporaryUploadPath is not null
                    && File.Exists(TemporaryUploadPath))
                {
                    File.Delete(TemporaryUploadPath);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
