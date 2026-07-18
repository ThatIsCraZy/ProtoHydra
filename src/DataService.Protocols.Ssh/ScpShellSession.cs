using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using DataService.Core.Diagnostics;
using DataService.Core.Events;
using DataService.Core.FileSystem;

namespace DataService.Protocols.Ssh;

/// <summary>
/// Minimal interactive-shell emulator for clients that drive SCP over a login shell
/// (notably WinSCP in "SCP" mode) instead of the exec channel. It implements just
/// enough of a POSIX shell — command sequencing with ';', $?/return-code expansion,
/// and the small command set WinSCP issues (pwd, cd, ls, groups, mkdir, rm, mv,
/// scp -t/-f, …) — to make directory browsing and transfers work. It is NOT a general
/// shell: unknown commands fail with 127 and no code is ever executed on the host.
/// </summary>
internal sealed partial class ScpShellSession
{
    private const int MaxLineLength = 64 * 1024;
    private const string BeginMarker = "WinSCP: this is begin-of-file";
    private const string EndMarker = "WinSCP: this is end-of-file";
    private const string Owner = "filehydra";
    private const string Group = "filehydra";

    private readonly Stream _input;
    private readonly Stream _output;
    private readonly string _rootPath;
    private readonly ITransferEventBus _eventBus;
    private readonly RootPathResolver _resolver;
    private readonly string? _username;
    private readonly IPAddress? _sourceAddress;

    private string _currentDirectory = "";
    private int _lastExitCode;

    public ScpShellSession(
        Stream input,
        Stream output,
        string rootPath,
        ITransferEventBus eventBus,
        string? username,
        IPAddress? sourceAddress = null)
    {
        _input = input;
        _output = output;
        _rootPath = rootPath;
        _eventBus = eventBus;
        _resolver = new RootPathResolver(rootPath);
        _username = username;
        _sourceAddress = sourceAddress;
    }

    /// <summary>True while a line-level SCP transfer wants the caller to keep the channel open.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                return;
            }

            if (await ProcessLineAsync(line, cancellationToken).ConfigureAwait(false))
            {
                return;
            }
        }
    }

    /// <summary>Processes one command line; returns true when the session should end (exit/logout).</summary>
    private async Task<bool> ProcessLineAsync(string line, CancellationToken cancellationToken)
    {
        foreach (var segment in SplitSegments(line))
        {
            var tokens = Tokenize(segment);
            if (tokens.Count == 0)
            {
                continue;
            }

            if (tokens[0] is "exit" or "logout" or "quit")
            {
                return true;
            }

            _lastExitCode = await ExecuteAsync(tokens, segment, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private async Task<int> ExecuteAsync(
        IReadOnlyList<string> tokens,
        string rawSegment,
        CancellationToken cancellationToken)
    {
        var command = tokens[0];
        var arguments = tokens.Skip(1).ToArray();
        return command switch
        {
            "echo" => await EchoAsync(arguments, cancellationToken).ConfigureAwait(false),
            "pwd" => await PwdAsync(cancellationToken).ConfigureAwait(false),
            "cd" => await ChangeDirectoryAsync(arguments, cancellationToken).ConfigureAwait(false),
            "ls" or "dir" => await ListAsync(arguments, cancellationToken).ConfigureAwait(false),
            "groups" => await WriteResultAsync(Group, 0, cancellationToken).ConfigureAwait(false),
            "id" => await WriteResultAsync($"uid=1000({Owner}) gid=1000({Group}) groups=1000({Group})", 0, cancellationToken).ConfigureAwait(false),
            "which" or "command" => await WhichAsync(arguments, cancellationToken).ConfigureAwait(false),
            "mkdir" => await MakeDirectoryAsync(arguments, cancellationToken).ConfigureAwait(false),
            "rm" => await RemoveAsync(arguments, cancellationToken).ConfigureAwait(false),
            "rmdir" => await RemoveAsync(arguments, cancellationToken).ConfigureAwait(false),
            "mv" => await MoveAsync(arguments, cancellationToken).ConfigureAwait(false),
            "scp" => await ScpAsync(rawSegment, cancellationToken).ConfigureAwait(false),
            // Metadata/no-op commands: accept silently so WinSCP's post-transfer steps succeed.
            "chmod" or "chgrp" or "chown" or "touch" or "umask"
                or "unset" or "unalias" or "alias" or "export" or "set" or "true" or ":" => 0,
            "false" => 1,
            "printenv" => await PrintEnvAsync(arguments, cancellationToken).ConfigureAwait(false),
            _ => await UnknownCommandAsync(command, cancellationToken).ConfigureAwait(false)
        };
    }

    private async Task<int> EchoAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        await WriteLineAsync(string.Join(' ', arguments), cancellationToken).ConfigureAwait(false);
        return 0;
    }

    private Task<int> PwdAsync(CancellationToken cancellationToken)
        => WriteResultAsync(ToVirtualPath(_currentDirectory), 0, cancellationToken);

    private async Task<int> ChangeDirectoryAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var target = arguments.Count == 0 ? "" : arguments[^1];
        if (!TryResolve(target, out var resolved, out var error))
        {
            await WriteLineAsync($"cd: {target}: {error}", cancellationToken).ConfigureAwait(false);
            return 1;
        }

        if (!Directory.Exists(resolved.FullPath))
        {
            await WriteLineAsync($"cd: {target}: No such file or directory", cancellationToken).ConfigureAwait(false);
            return 1;
        }

        _currentDirectory = resolved.RelativePath;
        return 0;
    }

    private async Task<int> ListAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var listSelf = false;
        string? target = null;
        foreach (var argument in arguments)
        {
            if (argument.StartsWith('-'))
            {
                if (argument.Contains('d', StringComparison.Ordinal))
                {
                    listSelf = true;
                }

                continue;
            }

            target = argument;
        }

        if (!TryResolve(target ?? "", out var resolved, out var error))
        {
            await WriteLineAsync($"ls: cannot access '{target}': {error}", cancellationToken).ConfigureAwait(false);
            return 2;
        }

        try
        {
            if (File.Exists(resolved.FullPath) && !Directory.Exists(resolved.FullPath))
            {
                await WriteLineAsync(FormatListing(new FileInfo(resolved.FullPath), Path.GetFileName(resolved.FullPath)), cancellationToken).ConfigureAwait(false);
                return 0;
            }

            if (!Directory.Exists(resolved.FullPath))
            {
                await WriteLineAsync($"ls: cannot access '{target}': No such file or directory", cancellationToken).ConfigureAwait(false);
                return 2;
            }

            if (listSelf)
            {
                await WriteLineAsync(FormatListing(new DirectoryInfo(resolved.FullPath), Path.GetFileName(resolved.FullPath.TrimEnd(Path.DirectorySeparatorChar))), cancellationToken).ConfigureAwait(false);
                return 0;
            }

            return await ListDirectoryContentsAsync(resolved, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PublishIoError("ls", resolved.RelativePath, resolved.FullPath, ex);
            await WriteLineAsync($"ls: cannot open directory '{target}': {IoErrorClassifier.Describe(IoErrorClassifier.Classify(ex, resolved.FullPath))}", cancellationToken).ConfigureAwait(false);
            return 2;
        }
    }

    private async Task<int> ListDirectoryContentsAsync(ResolvedPath resolved, CancellationToken cancellationToken)
    {
        var entries = Directory.EnumerateFileSystemEntries(resolved.FullPath)
            .OrderBy(item => Path.GetFileName(item), StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var builder = new StringBuilder();
        builder.Append("total ").Append(entries.Length.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        builder.Append(FormatListing(new DirectoryInfo(resolved.FullPath), ".")).Append("\r\n");
        builder.Append(FormatListing(ParentInfo(resolved.FullPath), "..")).Append("\r\n");

        foreach (var entry in entries)
        {
            // A single denied/vanished entry must not break the listing.
            try
            {
                FileSystemInfo info = Directory.Exists(entry) ? new DirectoryInfo(entry) : new FileInfo(entry);
                builder.Append(FormatListing(info, info.Name)).Append("\r\n");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        await WriteRawAsync(builder.ToString(), cancellationToken).ConfigureAwait(false);
        return 0;
    }

    private async Task<int> MakeDirectoryAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var target = arguments.LastOrDefault(argument => !argument.StartsWith('-'));
        if (target is null)
        {
            await WriteLineAsync("mkdir: missing operand", cancellationToken).ConfigureAwait(false);
            return 1;
        }

        if (!TryResolve(target, out var resolved, out var error))
        {
            await WriteLineAsync($"mkdir: cannot create directory '{target}': {error}", cancellationToken).ConfigureAwait(false);
            return 1;
        }

        try
        {
            if (Directory.Exists(resolved.FullPath) || File.Exists(resolved.FullPath))
            {
                await WriteLineAsync($"mkdir: cannot create directory '{target}': File exists", cancellationToken).ConfigureAwait(false);
                return 1;
            }

            Directory.CreateDirectory(resolved.FullPath);
            Publish(TransferEventKind.CommandReceived, "mkdir", resolved.RelativePath, TransferResult.Success, null);
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PublishIoError("mkdir", resolved.RelativePath, resolved.FullPath, ex);
            await WriteLineAsync($"mkdir: cannot create directory '{target}': {IoErrorClassifier.Describe(IoErrorClassifier.Classify(ex, resolved.FullPath))}", cancellationToken).ConfigureAwait(false);
            return 1;
        }
    }

    private async Task<int> RemoveAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var target = arguments.LastOrDefault(argument => !argument.StartsWith('-'));
        if (target is null)
        {
            await WriteLineAsync("rm: missing operand", cancellationToken).ConfigureAwait(false);
            return 1;
        }

        if (!TryResolve(target, out var resolved, out var error))
        {
            await WriteLineAsync($"rm: cannot remove '{target}': {error}", cancellationToken).ConfigureAwait(false);
            return 1;
        }

        if (string.IsNullOrEmpty(resolved.RelativePath))
        {
            await WriteLineAsync("rm: refusing to remove the root directory", cancellationToken).ConfigureAwait(false);
            return 1;
        }

        try
        {
            if (Directory.Exists(resolved.FullPath))
            {
                Directory.Delete(resolved.FullPath, recursive: true);
            }
            else if (File.Exists(resolved.FullPath))
            {
                File.Delete(resolved.FullPath);
            }
            else
            {
                // rm -f on a missing path succeeds silently, matching POSIX.
                return 0;
            }

            Publish(TransferEventKind.CommandReceived, "rm", resolved.RelativePath, TransferResult.Success, null);
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PublishIoError("rm", resolved.RelativePath, resolved.FullPath, ex);
            await WriteLineAsync($"rm: cannot remove '{target}': {IoErrorClassifier.Describe(IoErrorClassifier.Classify(ex, resolved.FullPath))}", cancellationToken).ConfigureAwait(false);
            return 1;
        }
    }

    private async Task<int> MoveAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var operands = arguments.Where(argument => !argument.StartsWith('-')).ToArray();
        if (operands.Length < 2)
        {
            await WriteLineAsync("mv: missing destination operand", cancellationToken).ConfigureAwait(false);
            return 1;
        }

        if (!TryResolve(operands[0], out var source, out var sourceError))
        {
            await WriteLineAsync($"mv: {sourceError}", cancellationToken).ConfigureAwait(false);
            return 1;
        }

        if (!TryResolve(operands[1], out var destination, out var destinationError))
        {
            await WriteLineAsync($"mv: {destinationError}", cancellationToken).ConfigureAwait(false);
            return 1;
        }

        try
        {
            if (Directory.Exists(source.FullPath))
            {
                Directory.Move(source.FullPath, destination.FullPath);
            }
            else if (File.Exists(source.FullPath))
            {
                File.Move(source.FullPath, destination.FullPath, overwrite: true);
            }
            else
            {
                await WriteLineAsync($"mv: cannot stat '{operands[0]}': No such file or directory", cancellationToken).ConfigureAwait(false);
                return 1;
            }

            Publish(TransferEventKind.CommandReceived, "mv", $"{source.RelativePath} -> {destination.RelativePath}", TransferResult.Success, null);
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PublishIoError("mv", source.RelativePath, source.FullPath, ex);
            await WriteLineAsync($"mv: cannot move '{operands[0]}': {IoErrorClassifier.Describe(IoErrorClassifier.Classify(ex, source.FullPath))}", cancellationToken).ConfigureAwait(false);
            return 1;
        }
    }

    private async Task<int> ScpAsync(string rawSegment, CancellationToken cancellationToken)
    {
        ScpCommand command;
        try
        {
            command = ScpCommandParser.Parse(rawSegment.Trim());
        }
        catch (Exception ex)
        {
            await new ScpProtocolStream(_input, _output)
                .WriteErrorAsync(ex.Message, fatal: true, cancellationToken).ConfigureAwait(false);
            return 1;
        }

        PublishCommand("scp (shell)", TransferResult.Success, rawSegment.Trim());
        try
        {
            var session = new ScpServerSession(_input, _output, _rootPath, _eventBus, _username, _sourceAddress);
            await session.RunAsync(command, cancellationToken, singleTransaction: true).ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex)
        {
            try
            {
                await new ScpProtocolStream(_input, _output)
                    .WriteErrorAsync(ex.Message, fatal: true, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
            }

            Publish(
                command.Download ? TransferEventKind.DownloadFailed : TransferEventKind.UploadFailed,
                "scp (shell)",
                command.Path,
                TransferResult.Failed,
                ex.Message);
            return 1;
        }
    }

    private async Task<int> WhichAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var name = arguments.LastOrDefault(argument => !argument.StartsWith('-'));
        if (name == "scp")
        {
            return await WriteResultAsync("/usr/bin/scp", 0, cancellationToken).ConfigureAwait(false);
        }

        return 1;
    }

    private async Task<int> PrintEnvAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        // Advertise a UTF-8 locale so WinSCP treats file names as UTF-8 (matching our handling).
        if (arguments.Count > 0 && arguments[0] == "LANG")
        {
            return await WriteResultAsync("en_US.UTF-8", 0, cancellationToken).ConfigureAwait(false);
        }

        return 0;
    }

    private async Task<int> UnknownCommandAsync(string command, CancellationToken cancellationToken)
    {
        await WriteLineAsync($"sh: {command}: command not found", cancellationToken).ConfigureAwait(false);
        return 127;
    }

    private bool TryResolve(string clientPath, out ResolvedPath resolved, out string? error)
    {
        resolved = null!;
        error = null;
        var combined = CombineWithCurrent(clientPath);
        try
        {
            resolved = _resolver.ResolveClientPath(combined, percentDecode: false);
            return true;
        }
        catch (PathResolutionException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private string CombineWithCurrent(string clientPath)
    {
        if (string.IsNullOrEmpty(clientPath) || clientPath == ".")
        {
            return _currentDirectory;
        }

        if (clientPath.StartsWith('/'))
        {
            return clientPath;
        }

        return string.IsNullOrEmpty(_currentDirectory)
            ? clientPath
            : $"{_currentDirectory}/{clientPath}";
    }

    private string ToVirtualPath(string relativePath)
        => string.IsNullOrEmpty(relativePath) ? "/" : "/" + relativePath;

    private static FileSystemInfo ParentInfo(string fullPath)
    {
        var parent = Directory.GetParent(fullPath);
        return parent is not null ? parent : new DirectoryInfo(fullPath);
    }

    private static string FormatListing(FileSystemInfo info, string displayName)
    {
        var isDirectory = info is DirectoryInfo;
        var length = info is FileInfo file ? file.Length : 4096L;
        var readOnly = (info.Attributes & FileAttributes.ReadOnly) != 0;
        var permissions = isDirectory
            ? "drwxr-xr-x"
            : readOnly ? "-r--r--r--" : "-rw-r--r--";
        var links = isDirectory ? 2 : 1;
        return string.Format(
            CultureInfo.InvariantCulture,
            "{0} {1,3} {2,-8} {3,-8} {4,8} {5} {6}",
            permissions,
            links,
            Owner,
            Group,
            length,
            FormatListingDate(info.LastWriteTimeUtc),
            displayName);
    }

    private static string FormatListingDate(DateTime lastWriteUtc)
    {
        var local = lastWriteUtc.ToLocalTime();
        var month = local.ToString("MMM", CultureInfo.InvariantCulture);
        var day = local.Day.ToString(CultureInfo.InvariantCulture).PadLeft(2);
        // GNU ls: show the clock for recent files, the year for files older than ~6 months.
        var recent = (DateTime.Now - local).TotalDays is >= 0 and < 182;
        var timeOrYear = recent
            ? local.ToString("HH:mm", CultureInfo.InvariantCulture)
            : local.Year.ToString(CultureInfo.InvariantCulture).PadLeft(5);
        return $"{month} {day} {timeOrYear}";
    }

    private static IEnumerable<string> SplitSegments(string line)
    {
        var current = new StringBuilder();
        char? quote = null;
        foreach (var ch in line)
        {
            if (quote is not null)
            {
                current.Append(ch);
                if (ch == quote)
                {
                    quote = null;
                }

                continue;
            }

            switch (ch)
            {
                case '\'' or '"':
                    quote = ch;
                    current.Append(ch);
                    break;
                case ';' or '\n':
                    yield return current.ToString();
                    current.Clear();
                    break;
                default:
                    current.Append(ch);
                    break;
            }
        }

        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }

    private List<string> Tokenize(string segment)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var hasToken = false;
        char? quote = null;

        for (var index = 0; index < segment.Length; index++)
        {
            var ch = segment[index];
            if (quote == '\'')
            {
                if (ch == '\'')
                {
                    quote = null;
                }
                else
                {
                    current.Append(ch);
                }

                continue;
            }

            if (quote == '"')
            {
                if (ch == '"')
                {
                    quote = null;
                }
                else if (ch == '$')
                {
                    current.Append(ExpandVariable(segment, ref index));
                }
                else
                {
                    current.Append(ch);
                }

                continue;
            }

            switch (ch)
            {
                case '\'' or '"':
                    quote = ch;
                    hasToken = true;
                    break;
                case '$':
                    current.Append(ExpandVariable(segment, ref index));
                    hasToken = true;
                    break;
                case ' ' or '\t' or '\r':
                    if (hasToken)
                    {
                        tokens.Add(current.ToString());
                        current.Clear();
                        hasToken = false;
                    }

                    break;
                default:
                    current.Append(ch);
                    hasToken = true;
                    break;
            }
        }

        if (hasToken)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }

    private string ExpandVariable(string segment, ref int index)
    {
        // index points at '$'. Support $?, ${?}, and $NAME (unknown names expand to empty).
        var next = index + 1;
        if (next >= segment.Length)
        {
            return "$";
        }

        if (segment[next] == '?')
        {
            index = next;
            return _lastExitCode.ToString(CultureInfo.InvariantCulture);
        }

        if (segment[next] == '{')
        {
            var close = segment.IndexOf('}', next + 1);
            if (close < 0)
            {
                return "$";
            }

            var name = segment[(next + 1)..close];
            index = close;
            return name == "?" ? _lastExitCode.ToString(CultureInfo.InvariantCulture) : "";
        }

        var start = next;
        while (next < segment.Length && (char.IsLetterOrDigit(segment[next]) || segment[next] == '_'))
        {
            next++;
        }

        var variableName = segment[start..next];
        index = next - 1;
        // $status is the csh return variable; leaving it empty makes WinSCP fall back to $?.
        return variableName == "?" ? _lastExitCode.ToString(CultureInfo.InvariantCulture) : "";
    }

    private async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        var bytes = new List<byte>();
        var single = new byte[1];
        while (bytes.Count < MaxLineLength)
        {
            int read;
            try
            {
                read = await _input.ReadAsync(single.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                return null;
            }

            if (read == 0)
            {
                return bytes.Count == 0 ? null : Decode(bytes);
            }

            if (single[0] == (byte)'\n')
            {
                return Decode(bytes);
            }

            bytes.Add(single[0]);
        }

        return Decode(bytes);
    }

    private static string Decode(List<byte> bytes)
    {
        if (bytes.Count > 0 && bytes[^1] == (byte)'\r')
        {
            bytes.RemoveAt(bytes.Count - 1);
        }

        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private Task<int> WriteResultAsync(string line, int exitCode, CancellationToken cancellationToken)
    {
        return WriteThenReturnAsync(line, exitCode, cancellationToken);

        async Task<int> WriteThenReturnAsync(string text, int code, CancellationToken token)
        {
            await WriteLineAsync(text, token).ConfigureAwait(false);
            return code;
        }
    }

    private Task WriteLineAsync(string line, CancellationToken cancellationToken)
        // Shell channels use CRLF line endings; WinSCP tolerates both and SSH.NET's
        // ShellStream.ReadLine requires CRLF.
        => WriteRawAsync(line + "\r\n", cancellationToken);

    private async Task WriteRawAsync(string text, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await _output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await _output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private void PublishCommand(string command, TransferResult result, string? message)
        => Publish(TransferEventKind.CommandReceived, command, null, result, message);

    private void PublishIoError(string command, string? relativePath, string? fullPath, Exception exception)
        => _eventBus.TryPublish(new TransferEvent(
            DateTimeOffset.UtcNow,
            ProtocolKind.Scp,
            TransferEventKind.RequestRejected,
            _sourceAddress,
            _username,
            command,
            relativePath,
            null,
            null,
            null,
            TransferResult.Failed,
            IoErrorClassifier.BuildMessage(exception, fullPath),
            null,
            IoErrorClassifier.Classify(exception, fullPath)));

    private void Publish(
        TransferEventKind eventKind,
        string command,
        string? relativePath,
        TransferResult result,
        string? message)
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
            null,
            null,
            result,
            message,
            null));
}
