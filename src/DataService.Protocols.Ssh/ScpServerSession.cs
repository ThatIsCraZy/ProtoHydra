using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using DataService.Core.Diagnostics;
using DataService.Core.Events;
using DataService.Core.FileSystem;

namespace DataService.Protocols.Ssh;

internal sealed partial class ScpServerSession
{
    private readonly ITransferEventBus _eventBus;
    private readonly ScpProtocolStream _protocol;
    private readonly RootPathResolver _resolver;
    private readonly string? _username;
    private readonly IPAddress? _sourceAddress;

    public ScpServerSession(
        Stream input,
        Stream output,
        string rootPath,
        ITransferEventBus eventBus,
        string? username,
        IPAddress? sourceAddress = null)
    {
        _protocol = new ScpProtocolStream(input, output);
        _resolver = new RootPathResolver(rootPath);
        _eventBus = eventBus;
        _username = username;
        _sourceAddress = sourceAddress;
    }

    private bool _singleTransaction;

    /// <param name="singleTransaction">
    /// When true (SCP-over-shell), the sink returns after one complete top-level entry
    /// instead of waiting for channel EOF, because the shell channel stays open for the
    /// return-code marker the client reads next.
    /// </param>
    public async Task RunAsync(
        ScpCommand command,
        CancellationToken cancellationToken,
        bool singleTransaction = false)
    {
        _singleTransaction = singleTransaction;
        if (command.Download)
        {
            await SendAsync(command, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await ReceiveAsync(command, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task SendAsync(
        ScpCommand command,
        CancellationToken cancellationToken)
    {
        var resolved = Resolve(command.Path);
        if (Directory.Exists(resolved.FullPath) && !command.Recursive)
        {
            throw new InvalidOperationException("SCP recursive option -r is required for directory downloads.");
        }

        if (!Directory.Exists(resolved.FullPath) && !File.Exists(resolved.FullPath))
        {
            throw new FileNotFoundException("SCP source path not found.", resolved.RelativePath);
        }

        await _protocol.ReadOkAsync(cancellationToken).ConfigureAwait(false);
        await SendEntryAsync(resolved.FullPath, resolved.RelativePath, command, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendEntryAsync(
        string fullPath,
        string relativePath,
        ScpCommand command,
        CancellationToken cancellationToken)
    {
        if (Directory.Exists(fullPath))
        {
            await SendDirectoryAsync(fullPath, relativePath, command, cancellationToken).ConfigureAwait(false);
            return;
        }

        await SendRegularFileAsync(fullPath, relativePath, command, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendRegularFileAsync(
        string fullPath,
        string relativePath,
        ScpCommand command,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        FileStream stream;
        string fileName;
        long length;
        DateTime lastWriteTimeUtc;
        DateTime lastAccessTimeUtc;
        try
        {
            // Open before any protocol bytes are sent so ACL, cloud-stub, and
            // device errors abort this file cleanly instead of mid-record.
            var file = new FileInfo(fullPath);
            fileName = file.Name;
            lastWriteTimeUtc = file.LastWriteTimeUtc;
            lastAccessTimeUtc = file.LastAccessTimeUtc;
            stream = new FileStream(
                file.FullName,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            length = stream.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw PublishIoFailure(TransferEventKind.DownloadFailed, "scp -f", relativePath, fullPath, ex);
        }

        await using (stream.ConfigureAwait(false))
        {
            if (command.PreserveTimes)
            {
                await WriteTimestampAsync(lastWriteTimeUtc, lastAccessTimeUtc, cancellationToken).ConfigureAwait(false);
            }

            Publish(TransferEventKind.DownloadStarted, "scp -f", relativePath, null, TransferResult.Success, null, null);
            await _protocol.WriteLineAsync(
                $"C0644 {length.ToString(CultureInfo.InvariantCulture)} {fileName}",
                cancellationToken).ConfigureAwait(false);
            await _protocol.ReadOkAsync(cancellationToken).ConfigureAwait(false);

            await _protocol.CopyFileToRemoteAsync(stream, length, cancellationToken).ConfigureAwait(false);
            await _protocol.ReadOkAsync(cancellationToken).ConfigureAwait(false);
            Publish(
                TransferEventKind.DownloadCompleted,
                "scp -f",
                relativePath,
                length,
                TransferResult.Success,
                null,
                Stopwatch.GetElapsedTime(started));
        }
    }

    private async Task SendDirectoryAsync(
        string fullPath,
        string relativePath,
        ScpCommand command,
        CancellationToken cancellationToken)
    {
        DirectoryInfo directory;
        DirectoryInfo[] childDirectories;
        FileInfo[] files;
        DateTime lastWriteTimeUtc;
        DateTime lastAccessTimeUtc;
        try
        {
            // Enumerate before any protocol bytes are sent so ACL and device errors
            // abort this directory cleanly instead of mid-record.
            directory = new DirectoryInfo(fullPath);
            lastWriteTimeUtc = directory.LastWriteTimeUtc;
            lastAccessTimeUtc = directory.LastAccessTimeUtc;
            childDirectories = directory.EnumerateDirectories().OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            files = directory.EnumerateFiles().OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw PublishIoFailure(TransferEventKind.DownloadFailed, "scp -f -r", relativePath, fullPath, ex);
        }

        if (command.PreserveTimes)
        {
            await WriteTimestampAsync(lastWriteTimeUtc, lastAccessTimeUtc, cancellationToken).ConfigureAwait(false);
        }

        Publish(TransferEventKind.CommandReceived, "scp -f -r", relativePath, null, TransferResult.Success, "Directory started.", null);
        await _protocol.WriteLineAsync($"D0755 0 {directory.Name}", cancellationToken).ConfigureAwait(false);
        await _protocol.ReadOkAsync(cancellationToken).ConfigureAwait(false);

        foreach (var childDirectory in childDirectories)
        {
            await SendDirectoryAsync(
                childDirectory.FullName,
                CombineRelative(relativePath, childDirectory.Name),
                command,
                cancellationToken).ConfigureAwait(false);
        }

        foreach (var file in files)
        {
            await SendRegularFileAsync(
                file.FullName,
                CombineRelative(relativePath, file.Name),
                command,
                cancellationToken).ConfigureAwait(false);
        }

        await _protocol.WriteLineAsync("E", cancellationToken).ConfigureAwait(false);
        await _protocol.ReadOkAsync(cancellationToken).ConfigureAwait(false);
        Publish(TransferEventKind.CommandReceived, "scp -f -r", relativePath, null, TransferResult.Success, "Directory completed.", null);
    }

    private async Task WriteTimestampAsync(
        DateTime lastWriteTimeUtc,
        DateTime lastAccessTimeUtc,
        CancellationToken cancellationToken)
    {
        var mtime = new DateTimeOffset(lastWriteTimeUtc).ToUnixTimeSeconds();
        var atime = new DateTimeOffset(lastAccessTimeUtc).ToUnixTimeSeconds();
        await _protocol.WriteLineAsync(
            $"T{mtime.ToString(CultureInfo.InvariantCulture)} 0 {atime.ToString(CultureInfo.InvariantCulture)} 0",
            cancellationToken).ConfigureAwait(false);
        await _protocol.ReadOkAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ReceiveAsync(
        ScpCommand command,
        CancellationToken cancellationToken)
    {
        var target = Resolve(command.Path);
        if (command.TargetShouldBeDirectory && !Directory.Exists(target.FullPath))
        {
            throw new DirectoryNotFoundException("SCP target must be an existing directory.");
        }

        await _protocol.WriteOkAsync(cancellationToken).ConfigureAwait(false);
        await ReceiveEntriesAsync(target, command, currentDirectory: null, cancellationToken).ConfigureAwait(false);
    }

    private async Task ReceiveEntriesAsync(
        ResolvedPath target,
        ScpCommand command,
        string? currentDirectory,
        CancellationToken cancellationToken)
    {
        ScpTimestamp? pendingTimestamp = null;
        var receivedAnyTopLevelEntry = false;
        while (true)
        {
            string line;
            try
            {
                line = await _protocol.ReadControlLineAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (EndOfStreamException) when (currentDirectory is null && receivedAnyTopLevelEntry)
            {
                return;
            }

            if (line == "E")
            {
                await _protocol.WriteOkAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            if (line.StartsWith('T'))
            {
                pendingTimestamp = ParseTimestamp(line);
                await _protocol.WriteOkAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (line.StartsWith('D'))
            {
                if (!command.Recursive)
                {
                    throw new InvalidOperationException("SCP recursive option -r is required for directory uploads.");
                }

                var record = ParseDirectoryRecord(line);
                // OpenSSH rename semantics: a top-level directory sent to a nonexistent
                // target is stored under the target name itself, not target/name.
                var directoryPath = currentDirectory is null && !Directory.Exists(target.FullPath)
                    ? target
                    : ResolveIncomingPath(
                        target,
                        currentDirectory,
                        record.Name,
                        forceChildOfTarget: true);
                try
                {
                    Directory.CreateDirectory(directoryPath.FullPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw PublishIoFailure(TransferEventKind.UploadFailed, "scp -t -r", directoryPath.RelativePath, directoryPath.FullPath, ex);
                }

                Publish(TransferEventKind.CommandReceived, "scp -t -r", directoryPath.RelativePath, null, TransferResult.Success, "Directory started.", null);
                await _protocol.WriteOkAsync(cancellationToken).ConfigureAwait(false);
                await ReceiveEntriesAsync(target, command, directoryPath.RelativePath, cancellationToken).ConfigureAwait(false);
                ApplyTimestamp(directoryPath.FullPath, pendingTimestamp);
                Publish(TransferEventKind.CommandReceived, "scp -t -r", directoryPath.RelativePath, null, TransferResult.Success, "Directory completed.", null);
                pendingTimestamp = null;
                if (currentDirectory is null)
                {
                    receivedAnyTopLevelEntry = true;
                    if (_singleTransaction || (!command.Recursive && !command.TargetShouldBeDirectory))
                    {
                        return;
                    }
                }

                continue;
            }

            if (line.StartsWith('C'))
            {
                var record = ParseFileRecord(line);
                var filePath = ResolveIncomingPath(
                    target,
                    currentDirectory,
                    record.Name,
                    forceChildOfTarget: currentDirectory is not null || Directory.Exists(target.FullPath));
                await ReceiveSingleFileAsync(filePath, record.Size, pendingTimestamp, cancellationToken).ConfigureAwait(false);
                pendingTimestamp = null;
                if (currentDirectory is null)
                {
                    receivedAnyTopLevelEntry = true;
                    if (_singleTransaction || (!command.Recursive && !command.TargetShouldBeDirectory))
                    {
                        return;
                    }
                }

                continue;
            }

            throw new InvalidOperationException("Unsupported SCP control record.");
        }
    }

    private async Task ReceiveSingleFileAsync(
        ResolvedPath filePath,
        long size,
        ScpTimestamp? timestamp,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var parent = Path.GetDirectoryName(filePath.FullPath);
        if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent))
        {
            throw new DirectoryNotFoundException("SCP target directory does not exist.");
        }

        var tempPath = Path.Combine(parent, $".{Path.GetFileName(filePath.FullPath)}.{Guid.NewGuid():N}.uploading");
        Publish(TransferEventKind.UploadStarted, "scp -t", filePath.RelativePath, size, TransferResult.Success, null, null);
        await _protocol.WriteOkAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using (var stream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await _protocol.CopyExactAsync(stream, size, cancellationToken).ConfigureAwait(false);
            }

            await _protocol.ReadOkAsync(cancellationToken).ConfigureAwait(false);
            File.Move(tempPath, filePath.FullPath, overwrite: true);
            ApplyTimestamp(filePath.FullPath, timestamp);
            await _protocol.WriteOkAsync(cancellationToken).ConfigureAwait(false);
            Publish(
                TransferEventKind.UploadCompleted,
                "scp -t",
                filePath.RelativePath,
                size,
                TransferResult.Success,
                null,
                Stopwatch.GetElapsedTime(started));
        }
        catch (Exception ex)
        {
            TryDeleteTemporaryFile(tempPath);
            if (ex is IOException or UnauthorizedAccessException)
            {
                throw PublishIoFailure(TransferEventKind.UploadFailed, "scp -t", filePath.RelativePath, filePath.FullPath, ex);
            }

            throw;
        }
    }

    private static void TryDeleteTemporaryFile(string tempPath)
    {
        try
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private InvalidOperationException PublishIoFailure(
        TransferEventKind eventKind,
        string command,
        string relativePath,
        string? fullPath,
        Exception exception)
    {
        var message = IoErrorClassifier.BuildMessage(exception, fullPath);
        Publish(
            eventKind,
            command,
            relativePath,
            null,
            TransferResult.Failed,
            message,
            null,
            IoErrorClassifier.Classify(exception, fullPath));
        return new InvalidOperationException(message, exception);
    }

    private ResolvedPath ResolveIncomingPath(
        ResolvedPath target,
        string? currentDirectory,
        string fileName,
        bool forceChildOfTarget)
    {
        ValidateReceivedFileName(fileName);
        if (!string.IsNullOrEmpty(currentDirectory))
        {
            return Resolve(CombineRelative(currentDirectory, fileName));
        }

        if (forceChildOfTarget)
        {
            return Resolve(CombineRelative(target.RelativePath, fileName));
        }

        return target;
    }

    private static ScpFileRecord ParseFileRecord(string line)
    {
        var match = FileRecordRegex().Match(line);
        if (!match.Success)
        {
            throw new InvalidOperationException("Invalid SCP file record.");
        }

        var fileName = match.Groups["name"].Value;
        ValidateReceivedFileName(fileName);
        if (!long.TryParse(match.Groups["size"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var size)
            || size < 0)
        {
            throw new InvalidOperationException("Invalid SCP file size.");
        }

        return new ScpFileRecord(size, fileName);
    }

    private static ScpDirectoryRecord ParseDirectoryRecord(string line)
    {
        var match = DirectoryRecordRegex().Match(line);
        if (!match.Success)
        {
            throw new InvalidOperationException("Invalid SCP directory record.");
        }

        var directoryName = match.Groups["name"].Value;
        ValidateReceivedFileName(directoryName);
        return new ScpDirectoryRecord(directoryName);
    }

    private static ScpTimestamp ParseTimestamp(string line)
    {
        var match = TimestampRecordRegex().Match(line);
        if (!match.Success)
        {
            throw new InvalidOperationException("Invalid SCP timestamp record.");
        }

        var modified = DateTimeOffset.FromUnixTimeSeconds(long.Parse(match.Groups["mtime"].Value, CultureInfo.InvariantCulture));
        var accessed = DateTimeOffset.FromUnixTimeSeconds(long.Parse(match.Groups["atime"].Value, CultureInfo.InvariantCulture));
        return new ScpTimestamp(modified, accessed);
    }

    private static void ApplyTimestamp(string path, ScpTimestamp? timestamp)
    {
        if (timestamp is null)
        {
            return;
        }

        // Best effort, matching OpenSSH: a failed utimes must not fail the transfer.
        try
        {
            if (Directory.Exists(path))
            {
                Directory.SetLastWriteTimeUtc(path, timestamp.Modified.UtcDateTime);
                Directory.SetLastAccessTimeUtc(path, timestamp.Accessed.UtcDateTime);
                return;
            }

            File.SetLastWriteTimeUtc(path, timestamp.Modified.UtcDateTime);
            File.SetLastAccessTimeUtc(path, timestamp.Accessed.UtcDateTime);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private ResolvedPath Resolve(string path)
    {
        try
        {
            return _resolver.ResolveClientPath(path.TrimStart('/'), percentDecode: false);
        }
        catch (PathResolutionException ex)
        {
            throw new UnauthorizedAccessException(ex.Message, ex);
        }
    }

    private static void ValidateReceivedFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)
            || fileName is "." or ".."
            || fileName.Contains('/')
            || fileName.Contains('\\')
            || fileName.Contains('\0')
            || fileName.Contains('\r')
            || fileName.Contains('\n')
            || fileName.Contains(':'))
        {
            throw new InvalidOperationException("Invalid SCP file name.");
        }
    }

    private static string CombineRelative(string basePath, string fileName)
        => string.IsNullOrEmpty(basePath) ? fileName : $"{basePath}/{fileName}";

    private void Publish(
        TransferEventKind eventKind,
        string command,
        string relativePath,
        long? byteCount,
        TransferResult result,
        string? message,
        TimeSpan? duration,
        IoErrorCategory? ioError = null)
        => _eventBus.TryPublish(new TransferEvent(
            DateTimeOffset.UtcNow,
            ProtocolKind.Scp,
            eventKind,
            _sourceAddress,
            _username,
            command,
            relativePath,
            eventKind is TransferEventKind.UploadStarted or TransferEventKind.UploadCompleted or TransferEventKind.UploadFailed
                ? TransferDirection.Upload
                : eventKind is TransferEventKind.DownloadStarted or TransferEventKind.DownloadCompleted or TransferEventKind.DownloadFailed
                    ? TransferDirection.Download
                    : null,
            byteCount,
            duration,
            result,
            message,
            null,
            ioError));

    private sealed record ScpTimestamp(DateTimeOffset Modified, DateTimeOffset Accessed);

    private sealed record ScpFileRecord(long Size, string Name);

    private sealed record ScpDirectoryRecord(string Name);

    [GeneratedRegex("^C(?<mode>[0-7]{4}) (?<size>[0-9]+) (?<name>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex FileRecordRegex();

    [GeneratedRegex("^D(?<mode>[0-7]{4}) 0 (?<name>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex DirectoryRecordRegex();

    [GeneratedRegex("^T(?<mtime>[0-9]+) 0 (?<atime>[0-9]+) 0$", RegexOptions.CultureInvariant)]
    private static partial Regex TimestampRecordRegex();
}
