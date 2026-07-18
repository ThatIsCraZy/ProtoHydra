using System.Diagnostics;
using System.Globalization;
using DataService.Core.Events;
using FubarDev.FtpServer;
using FubarDev.FtpServer.BackgroundTransfer;
using FubarDev.FtpServer.FileSystem;
using FubarDev.FtpServer.FileSystem.DotNet;
using Microsoft.Extensions.Options;

namespace DataService.Protocols.Ftp;

internal sealed class InstrumentedFtpFileSystemFactory : IFileSystemClassFactory
{
    private readonly FtpInstrumentationContext _instrumentation;
    private readonly DotNetFileSystemOptions _options;

    public InstrumentedFtpFileSystemFactory(
        IOptions<DotNetFileSystemOptions> options,
        FtpInstrumentationContext instrumentation)
    {
        _options = options.Value;
        _instrumentation = instrumentation;
    }

    public Task<IUnixFileSystem> Create(IAccountInformation accountInformation)
    {
        var rootPath = string.IsNullOrWhiteSpace(_options.RootPath)
            ? _instrumentation.RootPath
            : Path.GetFullPath(_options.RootPath);
        IUnixFileSystem inner = new DotNetFileSystem(
            rootPath,
            _options.AllowNonEmptyDirectoryDelete,
            _options.StreamBufferSize ?? 64 * 1024,
            _options.FlushAfterWrite);

        return Task.FromResult<IUnixFileSystem>(new InstrumentedFtpFileSystem(
            inner,
            _instrumentation,
            accountInformation.FtpUser?.Identity?.Name));
    }
}

internal sealed class InstrumentedFtpFileSystem : IUnixFileSystem
{
    private readonly IUnixFileSystem _inner;
    private readonly FtpInstrumentationContext _instrumentation;
    private readonly string? _username;

    public InstrumentedFtpFileSystem(
        IUnixFileSystem inner,
        FtpInstrumentationContext instrumentation,
        string? username)
    {
        _inner = inner;
        _instrumentation = instrumentation;
        _username = username;
    }

    public bool SupportsAppend => _inner.SupportsAppend;

    public bool SupportsNonEmptyDirectoryDelete => _inner.SupportsNonEmptyDirectoryDelete;

    public StringComparer FileSystemEntryComparer => _inner.FileSystemEntryComparer;

    public IUnixDirectoryEntry Root => _inner.Root;

    public async Task<IReadOnlyList<IUnixFileSystemEntry>> GetEntriesAsync(
        IUnixDirectoryEntry directoryEntry,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var correlationId = Guid.NewGuid().ToString("N");
        try
        {
            var entries = await _inner.GetEntriesAsync(directoryEntry, cancellationToken);
            Publish(
                TransferEventKind.DirectoryListed,
                "LIST",
                GetRelativePath(directoryEntry),
                null,
                null,
                Stopwatch.GetElapsedTime(started),
                TransferResult.Success,
                entries.Count.ToString(CultureInfo.InvariantCulture),
                correlationId);
            return entries;
        }
        catch (Exception ex)
        {
            Publish(
                TransferEventKind.RequestRejected,
                "LIST",
                GetRelativePath(directoryEntry),
                null,
                null,
                Stopwatch.GetElapsedTime(started),
                TransferResult.Failed,
                ex.Message,
                correlationId);
            throw;
        }
    }

    public Task<IUnixFileSystemEntry?> GetEntryByNameAsync(
        IUnixDirectoryEntry directoryEntry,
        string name,
        CancellationToken cancellationToken)
        => _inner.GetEntryByNameAsync(directoryEntry, name, cancellationToken);

    public Task<IUnixFileSystemEntry> MoveAsync(
        IUnixDirectoryEntry parent,
        IUnixFileSystemEntry source,
        IUnixDirectoryEntry target,
        string fileName,
        CancellationToken cancellationToken)
        => _inner.MoveAsync(parent, source, target, fileName, cancellationToken);

    public Task UnlinkAsync(IUnixFileSystemEntry entry, CancellationToken cancellationToken)
        => _inner.UnlinkAsync(entry, cancellationToken);

    public Task<IUnixDirectoryEntry> CreateDirectoryAsync(
        IUnixDirectoryEntry targetDirectory,
        string directoryName,
        CancellationToken cancellationToken)
        => _inner.CreateDirectoryAsync(targetDirectory, directoryName, cancellationToken);

    public async Task<Stream> OpenReadAsync(
        IUnixFileEntry fileEntry,
        long startPosition,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var correlationId = Guid.NewGuid().ToString("N");
        var relativePath = GetRelativePath(fileEntry);
        Publish(
            TransferEventKind.DownloadStarted,
            "RETR",
            relativePath,
            TransferDirection.Download,
            null,
            TimeSpan.Zero,
            TransferResult.Success,
            null,
            correlationId);

        try
        {
            var innerStream = await _inner.OpenReadAsync(fileEntry, startPosition, cancellationToken);
            return new CountingReadStream(
                innerStream,
                count => Publish(
                    TransferEventKind.DownloadCompleted,
                    "RETR",
                    relativePath,
                    TransferDirection.Download,
                    count,
                    Stopwatch.GetElapsedTime(started),
                    TransferResult.Success,
                    null,
                    correlationId),
                ex => Publish(
                    TransferEventKind.DownloadFailed,
                    "RETR",
                    relativePath,
                    TransferDirection.Download,
                    null,
                    Stopwatch.GetElapsedTime(started),
                    TransferResult.Failed,
                    ex.Message,
                    correlationId));
        }
        catch (Exception ex)
        {
            Publish(
                TransferEventKind.DownloadFailed,
                "RETR",
                relativePath,
                TransferDirection.Download,
                null,
                Stopwatch.GetElapsedTime(started),
                TransferResult.Failed,
                ex.Message,
                correlationId);
            throw;
        }
    }

    public async Task<IBackgroundTransfer?> AppendAsync(
        IUnixFileEntry fileEntry,
        long? startPosition,
        Stream data,
        CancellationToken cancellationToken)
    {
        var relativePath = GetRelativePath(fileEntry);
        var started = Stopwatch.GetTimestamp();
        var correlationId = PublishUploadStarted("APPE", relativePath);

        try
        {
            var transfer = await _inner.AppendAsync(fileEntry, startPosition, data, cancellationToken);
            if (transfer is null)
            {
                StartUploadCompletionMonitor("APPE", relativePath, started, correlationId);
                return null;
            }

            StartUploadCompletionMonitor("APPE", relativePath, started, correlationId);
            return new InstrumentedBackgroundTransfer(
                transfer,
                _instrumentation,
                _username,
                "APPE",
                relativePath,
                started,
                correlationId);
        }
        catch (Exception ex)
        {
            PublishUploadFailed("APPE", relativePath, started, correlationId, ex);
            throw;
        }
    }

    public async Task<IBackgroundTransfer?> CreateAsync(
        IUnixDirectoryEntry targetDirectory,
        string fileName,
        Stream data,
        CancellationToken cancellationToken)
    {
        var relativePath = GetRelativePath(targetDirectory, fileName);
        var started = Stopwatch.GetTimestamp();
        var correlationId = PublishUploadStarted("STOR", relativePath);

        try
        {
            var transfer = await _inner.CreateAsync(targetDirectory, fileName, data, cancellationToken);
            if (transfer is null)
            {
                StartUploadCompletionMonitor("STOR", relativePath, started, correlationId);
                return null;
            }

            StartUploadCompletionMonitor("STOR", relativePath, started, correlationId);
            return new InstrumentedBackgroundTransfer(
                transfer,
                _instrumentation,
                _username,
                "STOR",
                relativePath,
                started,
                correlationId);
        }
        catch (Exception ex)
        {
            PublishUploadFailed("STOR", relativePath, started, correlationId, ex);
            throw;
        }
    }

    public async Task<IBackgroundTransfer?> ReplaceAsync(
        IUnixFileEntry fileEntry,
        Stream data,
        CancellationToken cancellationToken)
    {
        var relativePath = GetRelativePath(fileEntry);
        var started = Stopwatch.GetTimestamp();
        var correlationId = PublishUploadStarted("STOR",
            relativePath);

        try
        {
            var transfer = await _inner.ReplaceAsync(fileEntry, data, cancellationToken);
            if (transfer is null)
            {
                StartUploadCompletionMonitor("STOR", relativePath, started, correlationId);
                return null;
            }

            StartUploadCompletionMonitor("STOR", relativePath, started, correlationId);
            return new InstrumentedBackgroundTransfer(
                transfer,
                _instrumentation,
                _username,
                "STOR",
                relativePath,
                started,
                correlationId);
        }
        catch (Exception ex)
        {
            PublishUploadFailed("STOR", relativePath, started, correlationId, ex);
            throw;
        }
    }

    public Task<IUnixFileSystemEntry> SetMacTimeAsync(
        IUnixFileSystemEntry entry,
        DateTimeOffset? modify,
        DateTimeOffset? access,
        DateTimeOffset? create,
        CancellationToken cancellationToken)
        => _inner.SetMacTimeAsync(entry, modify, access, create, cancellationToken);

    private string PublishUploadStarted(string command, string? relativePath)
    {
        var correlationId = Guid.NewGuid().ToString("N");
        Publish(
            TransferEventKind.UploadStarted,
            command,
            relativePath,
            TransferDirection.Upload,
            null,
            TimeSpan.Zero,
            TransferResult.Success,
            null,
            correlationId);
        return correlationId;
    }

    private void PublishUploadFailed(
        string command,
        string? relativePath,
        long started,
        string correlationId,
        Exception ex)
        => Publish(
            TransferEventKind.UploadFailed,
            command,
            relativePath,
            TransferDirection.Upload,
            null,
            Stopwatch.GetElapsedTime(started),
            TransferResult.Failed,
            ex.Message,
            correlationId);

    private void StartUploadCompletionMonitor(
        string command,
        string? relativePath,
        long started,
        string correlationId)
    {
        _ = Task.Run(async () =>
        {
            long? lastLength = null;
            var stableReads = 0;
            for (var attempt = 0; attempt < 100; attempt++)
            {
                await Task.Delay(100).ConfigureAwait(false);
                var length = TryGetFileLength(relativePath);
                if (length is null)
                {
                    stableReads = 0;
                    continue;
                }

                if (lastLength == length)
                {
                    stableReads++;
                }
                else
                {
                    stableReads = 0;
                    lastLength = length;
                }

                if (stableReads < 2)
                {
                    continue;
                }

                if (_instrumentation.TryMarkUploadCompleted(correlationId))
                {
                    Publish(
                        TransferEventKind.UploadCompleted,
                        command,
                        relativePath,
                        TransferDirection.Upload,
                        length,
                        Stopwatch.GetElapsedTime(started),
                        TransferResult.Success,
                        null,
                        correlationId);
                }

                return;
            }
        });
    }

    private string? GetRelativePath(IUnixFileSystemEntry entry)
    {
        if (entry is DotNetFileSystemEntry dotNetEntry)
        {
            return NormalizeRelativePath(dotNetEntry.Info.FullName);
        }

        return entry.Name;
    }

    private string? GetRelativePath(IUnixDirectoryEntry directory, string fileName)
    {
        if (directory is DotNetDirectoryEntry dotNetDirectory)
        {
            return NormalizeRelativePath(Path.Combine(dotNetDirectory.DirectoryInfo.FullName, fileName));
        }

        return fileName;
    }

    private string? NormalizeRelativePath(string fullPath)
    {
        var relative = Path.GetRelativePath(_instrumentation.RootPath, fullPath);
        return relative == "."
            ? string.Empty
            : relative.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
    }

    private long? TryGetFileLength(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        var fullPath = Path.GetFullPath(Path.Combine(
            _instrumentation.RootPath,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var rootPath = _instrumentation.RootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase)
            || !File.Exists(fullPath))
        {
            return null;
        }

        return new FileInfo(fullPath).Length;
    }

    private void Publish(
        TransferEventKind kind,
        string command,
        string? relativePath,
        TransferDirection? direction,
        long? byteCount,
        TimeSpan? duration,
        TransferResult result,
        string? message,
        string correlationId)
        => _instrumentation.EventBus.TryPublish(new TransferEvent(
            DateTimeOffset.UtcNow,
            _instrumentation.Protocol,
            kind,
            null,
            _username,
            command,
            relativePath,
            direction,
            byteCount,
            duration,
            result,
            message,
            correlationId));
}

internal sealed class InstrumentedBackgroundTransfer : IBackgroundTransfer
{
    private readonly IBackgroundTransfer _inner;
    private readonly FtpInstrumentationContext _instrumentation;
    private readonly string? _username;
    private readonly string _command;
    private readonly string? _relativePath;
    private readonly long _started;
    private readonly string _correlationId;

    public InstrumentedBackgroundTransfer(
        IBackgroundTransfer inner,
        FtpInstrumentationContext instrumentation,
        string? username,
        string command,
        string? relativePath,
        long started,
        string correlationId)
    {
        _inner = inner;
        _instrumentation = instrumentation;
        _username = username;
        _command = command;
        _relativePath = relativePath;
        _started = started;
        _correlationId = correlationId;
    }

    public string TransferId => _inner.TransferId;

    public async Task Start(IProgress<long> progress, CancellationToken cancellationToken)
    {
        var byteCount = 0L;
        var progressProxy = new Progress<long>(value =>
        {
            byteCount = Math.Max(byteCount, value);
            progress.Report(value);
        });

        try
        {
            await _inner.Start(progressProxy, cancellationToken);
            if (byteCount <= 0)
            {
                byteCount = TryGetUploadedFileLength() ?? 0;
            }

            Publish(TransferEventKind.UploadCompleted, byteCount, TransferResult.Success, null);
        }
        catch (Exception ex)
        {
            Publish(TransferEventKind.UploadFailed, byteCount > 0 ? byteCount : null, TransferResult.Failed, ex.Message);
            throw;
        }
    }

    public void Dispose()
    {
        if (_inner is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    private void Publish(
        TransferEventKind kind,
        long? byteCount,
        TransferResult result,
        string? message)
    {
        if (kind == TransferEventKind.UploadCompleted
            && !_instrumentation.TryMarkUploadCompleted(_correlationId))
        {
            return;
        }

        _instrumentation.EventBus.TryPublish(new TransferEvent(
            DateTimeOffset.UtcNow,
            _instrumentation.Protocol,
            kind,
            null,
            _username,
            _command,
            _relativePath,
            TransferDirection.Upload,
            byteCount,
            Stopwatch.GetElapsedTime(_started),
            result,
            message,
            _correlationId));
    }

    private long? TryGetUploadedFileLength()
    {
        if (string.IsNullOrWhiteSpace(_relativePath))
        {
            return null;
        }

        var fullPath = Path.GetFullPath(Path.Combine(
            _instrumentation.RootPath,
            _relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!fullPath.StartsWith(_instrumentation.RootPath, StringComparison.OrdinalIgnoreCase)
            || !File.Exists(fullPath))
        {
            return null;
        }

        return new FileInfo(fullPath).Length;
    }
}

internal sealed class CountingReadStream : Stream
{
    private readonly Stream _inner;
    private readonly Action<long> _completed;
    private readonly Action<Exception> _failed;
    private long _bytesRead;
    private bool _completedPublished;
    private bool _failedPublished;

    public CountingReadStream(
        Stream inner,
        Action<long> completed,
        Action<Exception> failed)
    {
        _inner = inner;
        _completed = completed;
        _failed = failed;
    }

    public override bool CanRead => _inner.CanRead;

    public override bool CanSeek => _inner.CanSeek;

    public override bool CanWrite => false;

    public override long Length => _inner.Length;

    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    public override void Flush()
        => _inner.Flush();

    public override int Read(byte[] buffer, int offset, int count)
    {
        try
        {
            var read = _inner.Read(buffer, offset, count);
            _bytesRead += read;
            return read;
        }
        catch (Exception ex)
        {
            PublishFailed(ex);
            throw;
        }
    }

    public override int Read(Span<byte> buffer)
    {
        try
        {
            var read = _inner.Read(buffer);
            _bytesRead += read;
            return read;
        }
        catch (Exception ex)
        {
            PublishFailed(ex);
            throw;
        }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        try
        {
            var read = await _inner.ReadAsync(buffer, cancellationToken);
            _bytesRead += read;
            return read;
        }
        catch (Exception ex)
        {
            PublishFailed(ex);
            throw;
        }
    }

    public override async Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        try
        {
            var read = await _inner.ReadAsync(buffer, offset, count, cancellationToken);
            _bytesRead += read;
            return read;
        }
        catch (Exception ex)
        {
            PublishFailed(ex);
            throw;
        }
    }

    public override long Seek(long offset, SeekOrigin origin)
        => _inner.Seek(offset, origin);

    public override void SetLength(long value)
        => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
        => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try
            {
                if (!_failedPublished)
                {
                    PublishCompleted();
                }
            }
            finally
            {
                _inner.Dispose();
            }
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        try
        {
            if (!_failedPublished)
            {
                PublishCompleted();
            }
        }
        finally
        {
            await _inner.DisposeAsync();
        }

        await base.DisposeAsync();
    }

    private void PublishCompleted()
    {
        if (_completedPublished)
        {
            return;
        }

        _completedPublished = true;
        _completed(_bytesRead);
    }

    private void PublishFailed(Exception ex)
    {
        if (_failedPublished)
        {
            return;
        }

        _failedPublished = true;
        _failed(ex);
    }
}
