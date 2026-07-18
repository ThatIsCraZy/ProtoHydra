using System.Collections.Concurrent;
using System.Net;
using DataService.Core.Diagnostics;
using DataService.Core.Events;
using DataService.Core.FileSystem;
using SFTP;
using SFTP.Enums;
using SFTP.Exceptions;
using SFTP.Models;

namespace DataService.Protocols.Ssh;

internal sealed class RootedSftpHandler : ISFTPHandler
{
    private readonly ConcurrentDictionary<SFTPHandle, HandleState> _handles = new();
    private readonly ITransferEventBus _eventBus;
    private readonly RootPathResolver _resolver;
    private readonly string? _username;
    private readonly IPAddress? _sourceAddress;

    public RootedSftpHandler(
        string rootPath,
        ITransferEventBus eventBus,
        string? username,
        IPAddress? sourceAddress = null)
    {
        _resolver = new RootPathResolver(rootPath);
        _eventBus = eventBus;
        _username = username;
        _sourceAddress = sourceAddress;
    }

    public Task<SFTPExtensions> Init(
        uint clientVersion,
        string user,
        SFTPExtensions extensions,
        CancellationToken cancellationToken = default)
    {
        Publish(TransferEventKind.ClientConnected, null, null, null, TransferResult.Success, $"SFTP v{Math.Min(clientVersion, 3)}");
        return Task.FromResult(SFTPExtensions.None);
    }

    public Task<SFTPHandle> Open(
        SFTPPath path,
        FileMode fileMode,
        FileAccess fileAccess,
        SFTPAttributes attributes,
        CancellationToken cancellationToken = default)
    {
        if (fileAccess.HasFlag(FileAccess.Write))
        {
            return OpenUploadAsync(path, fileMode, cancellationToken);
        }

        if (fileMode is not (FileMode.Open or FileMode.OpenOrCreate))
        {
            PublishRejected(path.Path, "Unsupported read open mode.");
            throw new SftpStatusException(Status.PermissionDenied);
        }

        var resolved = ResolveExistingFile(path);
        FileStream stream;
        try
        {
            stream = new FileStream(
                resolved.FullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw PublishIoFailure(TransferEventKind.DownloadFailed, "OPEN", resolved.RelativePath, resolved.FullPath, ex);
        }

        var handle = CreateHandle();
        _handles[handle] = new HandleState(resolved.RelativePath, stream);
        Publish(TransferEventKind.DownloadStarted, "OPEN", resolved.RelativePath, null, TransferResult.Success, null);
        return Task.FromResult(handle);
    }

    public Task Close(SFTPHandle handle, CancellationToken cancellationToken = default)
    {
        if (!_handles.TryRemove(handle, out var state))
        {
            throw new HandleNotFoundException(handle);
        }

        try
        {
            if (state.Direction == TransferDirection.Upload)
            {
                state.Dispose();
                if (state.TemporaryPath is null || state.FinalPath is null)
                {
                    throw new InvalidOperationException("SFTP upload target was not initialized.");
                }

                File.Move(state.TemporaryPath, state.FinalPath, overwrite: true);
                ApplyPendingTimes(state);
                Publish(
                    TransferEventKind.UploadCompleted,
                    "CLOSE",
                    state.RelativePath,
                    new FileInfo(state.FinalPath).Length,
                    TransferResult.Success,
                    null);
                return Task.CompletedTask;
            }

            state.Dispose();
            if (state.Stream is not null)
            {
                Publish(
                    TransferEventKind.DownloadCompleted,
                    "CLOSE",
                    state.RelativePath,
                    state.BytesTransferred,
                    TransferResult.Success,
                    null);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            state.DeleteTemporaryUpload();
            Publish(
                TransferEventKind.UploadFailed,
                "CLOSE",
                state.RelativePath,
                state.BytesTransferred,
                TransferResult.Failed,
                IoErrorClassifier.BuildMessage(ex, state.FinalPath),
                IoErrorClassifier.Classify(ex, state.FinalPath));
            throw new SftpStatusException(Status.Failure);
        }

        return Task.CompletedTask;
    }

    public async Task<SFTPData> Read(
        SFTPHandle handle,
        ulong offset,
        uint length,
        CancellationToken cancellationToken = default)
    {
        if (!_handles.TryGetValue(handle, out var state) || state.Stream is null)
        {
            throw new HandleNotFoundException(handle);
        }

        if (state.Direction != TransferDirection.Download)
        {
            PublishRejected(state.RelativePath, "Read is not allowed on an upload handle.");
            throw new SftpStatusException(Status.PermissionDenied);
        }

        try
        {
            if (offset >= (ulong)state.Stream.Length)
            {
                return SFTPData.EOF;
            }

            var count = (int)Math.Min(length, int.MaxValue);
            var buffer = new byte[count];
            state.Stream.Position = (long)offset;
            var bytesRead = await state.Stream.ReadAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            state.BytesTransferred += bytesRead;
            return new SFTPData(buffer[..bytesRead]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw PublishIoFailure(TransferEventKind.DownloadFailed, "READ", state.RelativePath, null, ex);
        }
    }

    public Task<SFTPAttributes> LStat(SFTPPath path, CancellationToken cancellationToken = default)
        => Stat(path, cancellationToken);

    public Task<SFTPAttributes> FStat(SFTPHandle handle, CancellationToken cancellationToken = default)
    {
        if (!_handles.TryGetValue(handle, out var state))
        {
            throw new HandleNotFoundException(handle);
        }

        if (state.Direction == TransferDirection.Upload && state.Stream is not null)
        {
            // The final path does not exist until Close moves the temp file; report the in-progress size.
            state.Stream.Flush();
            var attributes = SFTPAttributes.DummyFile with
            {
                FileSize = (ulong)state.Stream.Length,
                LastAccessedTime = DateTimeOffset.UtcNow,
                LastModifiedTime = DateTimeOffset.UtcNow
            };
            return Task.FromResult(attributes);
        }

        var resolved = _resolver.ResolveClientPath(state.RelativePath, percentDecode: false);
        return Task.FromResult(SafeAttributes(ToFileSystemInfo(resolved.FullPath)));
    }

    public Task<SFTPHandle> OpenDir(SFTPPath path, CancellationToken cancellationToken = default)
    {
        var resolved = ResolveExistingDirectory(path);
        string[] directoryEntries;
        try
        {
            directoryEntries = Directory.EnumerateFileSystemEntries(resolved.FullPath).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw PublishIoFailure(TransferEventKind.CommandReceived, "OPENDIR", resolved.RelativePath, resolved.FullPath, ex);
        }

        var handle = CreateHandle();
        _handles[handle] = new HandleState(resolved.RelativePath, directoryEntries);
        Publish(TransferEventKind.CommandReceived, "OPENDIR", resolved.RelativePath, null, TransferResult.Success, null);
        return Task.FromResult(handle);
    }

    public Task<IEnumerable<SFTPName>> ReadDir(SFTPHandle handle, CancellationToken cancellationToken = default)
    {
        if (!_handles.TryGetValue(handle, out var state) || state.DirectoryEntries is null)
        {
            throw new HandleNotFoundException(handle);
        }

        // A single unreadable entry (deleted meanwhile, denied ACL, offline stub) must
        // not break the whole listing; such entries fall back to dummy attributes.
        var entries = state.DirectoryEntries
            .Select(ToFileSystemInfo)
            .Select(info => new SFTPName(info.Name, SafeAttributes(info)))
            .ToArray();
        Publish(
            TransferEventKind.DirectoryListed,
            "READDIR",
            state.RelativePath,
            entries.Length,
            TransferResult.Success,
            null);
        return Task.FromResult<IEnumerable<SFTPName>>(entries);
    }

    public Task<SFTPPath> RealPath(SFTPPath path, CancellationToken cancellationToken = default)
    {
        var resolved = Resolve(path);
        var virtualPath = string.IsNullOrEmpty(resolved.RelativePath)
            ? "/"
            : "/" + resolved.RelativePath;
        Publish(TransferEventKind.CommandReceived, "REALPATH", resolved.RelativePath, null, TransferResult.Success, null);
        return Task.FromResult(new SFTPPath(virtualPath));
    }

    public Task<SFTPAttributes> Stat(SFTPPath path, CancellationToken cancellationToken = default)
    {
        var resolved = Resolve(path);
        if (!Directory.Exists(resolved.FullPath) && !File.Exists(resolved.FullPath))
        {
            throw PublishPathNotFound("STAT", path, resolved.RelativePath);
        }

        var attributes = SafeAttributes(ToFileSystemInfo(resolved.FullPath));
        Publish(TransferEventKind.CommandReceived, "STAT", resolved.RelativePath, null, TransferResult.Success, null);
        return Task.FromResult(attributes);
    }

    public Task Write(SFTPHandle handle, ulong offset, byte[] data, CancellationToken cancellationToken = default)
    {
        if (!_handles.TryGetValue(handle, out var state)
            || state.Stream is null
            || state.Direction != TransferDirection.Upload)
        {
            throw new HandleNotFoundException(handle);
        }

        try
        {
            state.Stream.Position = checked((long)offset);
            state.Stream.Write(data, 0, data.Length);
            state.BytesTransferred = Math.Max(state.BytesTransferred, checked((long)offset + data.Length));
            return Task.CompletedTask;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OverflowException)
        {
            state.DeleteTemporaryUpload();
            Publish(
                TransferEventKind.UploadFailed,
                "WRITE",
                state.RelativePath,
                state.BytesTransferred,
                TransferResult.Failed,
                IoErrorClassifier.BuildMessage(ex, state.TemporaryPath),
                IoErrorClassifier.Classify(ex, state.TemporaryPath));
            throw new SftpStatusException(Status.Failure);
        }
    }

    public Task SetStat(SFTPPath path, SFTPAttributes attributes, CancellationToken cancellationToken = default)
    {
        var resolved = Resolve(path);
        if (!Directory.Exists(resolved.FullPath) && !File.Exists(resolved.FullPath))
        {
            throw new PathNotFoundException(path);
        }

        try
        {
            ApplyTimes(resolved.FullPath, attributes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw PublishIoFailure(TransferEventKind.RequestRejected, "SETSTAT", resolved.RelativePath, resolved.FullPath, ex);
        }

        Publish(TransferEventKind.CommandReceived, "SETSTAT", resolved.RelativePath, null, TransferResult.Success, null);
        return Task.CompletedTask;
    }

    public Task FSetStat(SFTPHandle handle, SFTPAttributes attributes, CancellationToken cancellationToken = default)
    {
        if (!_handles.TryGetValue(handle, out var state))
        {
            throw new HandleNotFoundException(handle);
        }

        if (state.Direction == TransferDirection.Upload)
        {
            // The temp file is still open and gets moved on Close; apply the times afterwards.
            state.PendingModifiedTime = GetSetTime(attributes.LastModifiedTime) ?? state.PendingModifiedTime;
            state.PendingAccessedTime = GetSetTime(attributes.LastAccessedTime) ?? state.PendingAccessedTime;
            Publish(TransferEventKind.CommandReceived, "FSETSTAT", state.RelativePath, null, TransferResult.Success, null);
            return Task.CompletedTask;
        }

        var resolved = _resolver.ResolveClientPath(state.RelativePath, percentDecode: false);
        try
        {
            ApplyTimes(resolved.FullPath, attributes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw PublishIoFailure(TransferEventKind.RequestRejected, "FSETSTAT", state.RelativePath, resolved.FullPath, ex);
        }

        Publish(TransferEventKind.CommandReceived, "FSETSTAT", state.RelativePath, null, TransferResult.Success, null);
        return Task.CompletedTask;
    }

    public Task Remove(SFTPPath path, CancellationToken cancellationToken = default)
        => RejectWriteAsync("REMOVE");

    public Task MakeDir(SFTPPath path, SFTPAttributes attributes, CancellationToken cancellationToken = default)
    {
        var resolved = Resolve(path);
        if (Directory.Exists(resolved.FullPath) || File.Exists(resolved.FullPath))
        {
            PublishRejected(resolved.RelativePath, "Directory already exists.");
            throw new SftpStatusException(Status.Failure);
        }

        var parent = Path.GetDirectoryName(resolved.FullPath);
        if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent))
        {
            PublishRejected(resolved.RelativePath, "Parent directory does not exist.");
            throw new SftpStatusException(Status.NoSuchFile);
        }

        try
        {
            Directory.CreateDirectory(resolved.FullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw PublishIoFailure(TransferEventKind.RequestRejected, "MKDIR", resolved.RelativePath, resolved.FullPath, ex);
        }

        Publish(TransferEventKind.CommandReceived, "MKDIR", resolved.RelativePath, null, TransferResult.Success, null);
        return Task.CompletedTask;
    }

    public Task RemoveDir(SFTPPath path, CancellationToken cancellationToken = default)
        => RejectWriteAsync("RMDIR");

    public Task Rename(SFTPPath oldPath, SFTPPath newPath, CancellationToken cancellationToken = default)
    {
        var oldResolved = Resolve(oldPath);
        var newResolved = Resolve(newPath);
        if (string.IsNullOrEmpty(oldResolved.RelativePath))
        {
            PublishRejected(oldResolved.RelativePath, "Root folder cannot be renamed.");
            throw new SftpStatusException(Status.PermissionDenied);
        }

        var isDirectory = Directory.Exists(oldResolved.FullPath);
        if (!isDirectory && !File.Exists(oldResolved.FullPath))
        {
            throw PublishPathNotFound("RENAME", oldPath, oldResolved.RelativePath);
        }

        if (Directory.Exists(newResolved.FullPath) || File.Exists(newResolved.FullPath))
        {
            PublishRejected(newResolved.RelativePath, "Rename target already exists.");
            throw new SftpStatusException(Status.Failure);
        }

        var parent = Path.GetDirectoryName(newResolved.FullPath);
        if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent))
        {
            PublishRejected(newResolved.RelativePath, "Rename target directory does not exist.");
            throw new SftpStatusException(Status.NoSuchFile);
        }

        try
        {
            if (isDirectory)
            {
                Directory.Move(oldResolved.FullPath, newResolved.FullPath);
            }
            else
            {
                File.Move(oldResolved.FullPath, newResolved.FullPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw PublishIoFailure(TransferEventKind.RequestRejected, "RENAME", oldResolved.RelativePath, oldResolved.FullPath, ex);
        }

        Publish(
            TransferEventKind.CommandReceived,
            "RENAME",
            oldResolved.RelativePath,
            null,
            TransferResult.Success,
            $"Renamed to {newResolved.RelativePath}.");
        return Task.CompletedTask;
    }

    public Task SymLink(SFTPPath linkPath, SFTPPath targetPath, CancellationToken cancellationToken = default)
        => RejectWriteAsync("SYMLINK");

    public Task<SFTPName> ReadLink(SFTPPath path, CancellationToken cancellationToken = default)
    {
        // Answer path-based like a POSIX server (OpenSSH) instead of OP_UNSUPPORTED:
        // clients treat per-path errors as a normal file outcome, while an unsupported
        // operation can abort automated workflows entirely. The root is guaranteed
        // symlink-free (reparse points are rejected), so an existing path is never a
        // symlink — readlink() on it would yield EINVAL, i.e. a plain Failure.
        var resolved = Resolve(path);
        if (!Directory.Exists(resolved.FullPath) && !File.Exists(resolved.FullPath))
        {
            throw PublishPathNotFound("READLINK", path, resolved.RelativePath);
        }

        Publish(
            TransferEventKind.CommandReceived,
            "READLINK",
            resolved.RelativePath,
            null,
            TransferResult.Failed,
            "Not a symbolic link.");
        throw new SftpStatusException(Status.Failure);
    }

    public Task Extended(string name, Stream inStream, Stream outStream)
    {
        Publish(
            TransferEventKind.RequestRejected,
            "EXTENDED",
            null,
            null,
            TransferResult.Rejected,
            $"SFTP extended request '{name}' is not supported.");
        throw new SftpStatusException(Status.OperationUnsupported);
    }

    private ResolvedPath Resolve(SFTPPath path)
    {
        try
        {
            var clientPath = NormalizeSftpPath(path.Path);
            return _resolver.ResolveClientPath(clientPath, percentDecode: false);
        }
        catch (PathResolutionException)
        {
            PublishRejected(path.Path, "Path rejected.");
            throw new SftpStatusException(Status.PermissionDenied);
        }
    }

    private ResolvedPath ResolveExistingFile(SFTPPath path)
    {
        var resolved = Resolve(path);
        if (!File.Exists(resolved.FullPath) || Directory.Exists(resolved.FullPath))
        {
            throw PublishPathNotFound("OPEN", path, resolved.RelativePath);
        }

        return resolved;
    }

    private ResolvedPath ResolveExistingDirectory(SFTPPath path)
    {
        var resolved = Resolve(path);
        if (!Directory.Exists(resolved.FullPath))
        {
            throw PublishPathNotFound("OPENDIR", path, resolved.RelativePath);
        }

        return resolved;
    }

    private PathNotFoundException PublishPathNotFound(string command, SFTPPath path, string? relativePath)
    {
        // Failed lookups must be visible in the capture log; "which file does the
        // client expect?" is the key question when a device-side workflow aborts.
        Publish(
            TransferEventKind.CommandReceived,
            command,
            relativePath ?? path.Path,
            null,
            TransferResult.Failed,
            "No such file or directory.");
        return new PathNotFoundException(path);
    }

    private Task<SFTPHandle> OpenUploadAsync(
        SFTPPath path,
        FileMode fileMode,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (fileMode is not (FileMode.Create or FileMode.CreateNew or FileMode.OpenOrCreate or FileMode.Truncate))
        {
            PublishRejected(path.Path, "Unsupported SFTP upload open mode.");
            throw new SftpStatusException(Status.PermissionDenied);
        }

        var resolved = Resolve(path);
        var parent = Path.GetDirectoryName(resolved.FullPath);
        if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent))
        {
            PublishRejected(resolved.RelativePath, "Target directory does not exist.");
            throw new SftpStatusException(Status.NoSuchFile);
        }

        if (Directory.Exists(resolved.FullPath))
        {
            PublishRejected(resolved.RelativePath, "Upload target is a directory.");
            throw new SftpStatusException(Status.PermissionDenied);
        }

        if (fileMode == FileMode.CreateNew && File.Exists(resolved.FullPath))
        {
            PublishRejected(resolved.RelativePath, "Upload target already exists.");
            throw new SftpStatusException(Status.Failure);
        }

        var tempPath = Path.Combine(
            parent,
            $".{Path.GetFileName(resolved.FullPath)}.{Guid.NewGuid():N}.uploading");
        try
        {
            var stream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var handle = CreateHandle();
            _handles[handle] = new HandleState(resolved.RelativePath, stream, tempPath, resolved.FullPath);
            Publish(TransferEventKind.UploadStarted, "OPEN", resolved.RelativePath, null, TransferResult.Success, null);
            return Task.FromResult(handle);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Publish(
                TransferEventKind.UploadFailed,
                "OPEN",
                resolved.RelativePath,
                null,
                TransferResult.Failed,
                IoErrorClassifier.BuildMessage(ex, tempPath),
                IoErrorClassifier.Classify(ex, tempPath));
            throw new SftpStatusException(Status.Failure);
        }
    }

    private static void ApplyTimes(string fullPath, SFTPAttributes attributes)
    {
        var modified = GetSetTime(attributes.LastModifiedTime);
        var accessed = GetSetTime(attributes.LastAccessedTime);
        if (Directory.Exists(fullPath))
        {
            if (modified is not null)
            {
                Directory.SetLastWriteTimeUtc(fullPath, modified.Value.UtcDateTime);
            }

            if (accessed is not null)
            {
                Directory.SetLastAccessTimeUtc(fullPath, accessed.Value.UtcDateTime);
            }

            return;
        }

        if (modified is not null)
        {
            File.SetLastWriteTimeUtc(fullPath, modified.Value.UtcDateTime);
        }

        if (accessed is not null)
        {
            File.SetLastAccessTimeUtc(fullPath, accessed.Value.UtcDateTime);
        }
    }

    private static void ApplyPendingTimes(HandleState state)
    {
        if (state.FinalPath is null)
        {
            return;
        }

        // Best effort: a failed timestamp update must not fail the completed upload.
        try
        {
            if (state.PendingModifiedTime is not null)
            {
                File.SetLastWriteTimeUtc(state.FinalPath, state.PendingModifiedTime.Value.UtcDateTime);
            }

            if (state.PendingAccessedTime is not null)
            {
                File.SetLastAccessTimeUtc(state.FinalPath, state.PendingAccessedTime.Value.UtcDateTime);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private SftpStatusException PublishIoFailure(
        TransferEventKind eventKind,
        string command,
        string? relativePath,
        string? fullPath,
        Exception exception)
    {
        var category = IoErrorClassifier.Classify(exception, fullPath);
        Publish(
            eventKind,
            command,
            relativePath,
            null,
            TransferResult.Failed,
            IoErrorClassifier.BuildMessage(exception, fullPath),
            category);
        return new SftpStatusException(category switch
        {
            IoErrorCategory.AccessDenied => Status.PermissionDenied,
            IoErrorCategory.PathNotFound => Status.NoSuchFile,
            _ => Status.Failure
        });
    }

    private static SFTPAttributes SafeAttributes(FileSystemInfo info)
    {
        try
        {
            return SFTPAttributes.FromFileSystemInfo(info);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return info is DirectoryInfo ? SFTPAttributes.DummyDirectory : SFTPAttributes.DummyFile;
        }
    }

    private static DateTimeOffset? GetSetTime(DateTimeOffset value)
        // Absent SFTP time attributes arrive as MinValue/epoch; treat those as "not set".
        => value > DateTimeOffset.UnixEpoch ? value : null;

    private static string NormalizeSftpPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path == ".")
        {
            return "";
        }

        return path.TrimStart('/');
    }

    private static SFTPAttributes ToAttributes(string fullPath)
    {
        return SFTPAttributes.FromFileSystemInfo(ToFileSystemInfo(fullPath));
    }

    private static FileSystemInfo ToFileSystemInfo(string fullPath)
        => Directory.Exists(fullPath)
            ? new DirectoryInfo(fullPath)
            : new FileInfo(fullPath);

    private static SFTPHandle CreateHandle()
        => new(Guid.NewGuid().ToString("N"));

    private Task RejectWriteAsync(string command)
    {
        Publish(TransferEventKind.RequestRejected, command, null, null, TransferResult.Rejected, "SFTP operation is not supported by this server.");
        throw new SftpStatusException(Status.PermissionDenied);
    }

    private void PublishRejected(string? path, string message)
        => Publish(TransferEventKind.RequestRejected, null, path, null, TransferResult.Rejected, message);

    private void Publish(
        TransferEventKind eventKind,
        string? command,
        string? relativePath,
        long? byteCount,
        TransferResult result,
        string? message,
        IoErrorCategory? ioError = null)
        => _eventBus.TryPublish(new TransferEvent(
            DateTimeOffset.UtcNow,
            ProtocolKind.Sftp,
            eventKind,
            _sourceAddress,
            _username,
            command,
            relativePath,
            eventKind is TransferEventKind.DownloadStarted or TransferEventKind.DownloadCompleted or TransferEventKind.DownloadFailed
                ? TransferDirection.Download
                : eventKind is TransferEventKind.UploadStarted or TransferEventKind.UploadCompleted or TransferEventKind.UploadFailed
                    ? TransferDirection.Upload
                    : null,
            byteCount,
            null,
            result,
            message,
            null,
            ioError));

    private sealed class HandleState : IDisposable
    {
        public HandleState(string relativePath, Stream stream)
        {
            RelativePath = relativePath;
            Stream = stream;
            Direction = TransferDirection.Download;
        }

        public HandleState(
            string relativePath,
            Stream stream,
            string temporaryPath,
            string finalPath)
        {
            RelativePath = relativePath;
            Stream = stream;
            TemporaryPath = temporaryPath;
            FinalPath = finalPath;
            Direction = TransferDirection.Upload;
        }

        public HandleState(string relativePath, string[] directoryEntries)
        {
            RelativePath = relativePath;
            DirectoryEntries = directoryEntries;
        }

        public string RelativePath { get; }

        public Stream? Stream { get; }

        public string[]? DirectoryEntries { get; }

        public TransferDirection? Direction { get; }

        public string? TemporaryPath { get; }

        public string? FinalPath { get; }

        public long BytesTransferred { get; set; }

        public DateTimeOffset? PendingModifiedTime { get; set; }

        public DateTimeOffset? PendingAccessedTime { get; set; }

        public void Dispose()
        {
            try
            {
                Stream?.Dispose();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Flushing to a vanished device must not mask the original error.
            }
        }

        public void DeleteTemporaryUpload()
        {
            Dispose();
            try
            {
                if (TemporaryPath is not null && File.Exists(TemporaryPath))
                {
                    File.Delete(TemporaryPath);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
