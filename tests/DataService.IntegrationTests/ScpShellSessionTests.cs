using System.Text;
using System.Threading.Channels;
using DataService.Core.Events;
using DataService.Protocols.Ssh;

namespace DataService.IntegrationTests;

/// <summary>
/// Drives <see cref="ScpShellSession"/> directly over in-memory duplex streams, replaying
/// the exact byte protocol WinSCP's "SCP" (shell) mode speaks: command lines wrapped with
/// begin/end markers and $? return codes, plus the scp -t/-f binary sub-protocol.
/// </summary>
public sealed class ScpShellSessionTests
{
    private const string BeginMarker = "WinSCP: this is begin-of-file";
    private const string EndMarker = "WinSCP: this is end-of-file:";

    private static string CreateRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "hydra-scpshell", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static (Task Session, WinScpShellDriver Driver) StartSession(string rootPath)
    {
        var clientToServer = new LoopbackStream();
        var serverToClient = new LoopbackStream();
        var session = new ScpShellSession(clientToServer, serverToClient, rootPath, new TransferEventBus(), "winscp-user");
        var task = session.RunAsync(CancellationToken.None);
        return (task, new WinScpShellDriver(clientToServer, serverToClient));
    }

    [Fact]
    public async Task ReturnVariableDetection_StatusEmpty_QuestionMarkNumeric()
    {
        var (session, driver) = StartSession(CreateRoot());

        var (statusOutput, _) = await driver.RunCommandAsync("echo \"$status\"");
        var (questionOutput, code) = await driver.RunCommandAsync("echo \"$?\"");

        Assert.Equal("", statusOutput.Trim());
        Assert.Equal("0", questionOutput.Trim());
        Assert.Equal(0, code);

        await driver.CloseAsync(session);
    }

    [Fact]
    public async Task Pwd_ReturnsVirtualRoot()
    {
        var (session, driver) = StartSession(CreateRoot());

        var (output, code) = await driver.RunCommandAsync("pwd");

        Assert.Equal("/", output.Trim());
        Assert.Equal(0, code);
        await driver.CloseAsync(session);
    }

    [Fact]
    public async Task Ls_ProducesUnixLongListing_WithFilesAndDirectories()
    {
        var root = CreateRoot();
        await File.WriteAllTextAsync(Path.Combine(root, "readme.txt"), "hello");
        Directory.CreateDirectory(Path.Combine(root, "subdir"));
        var (session, driver) = StartSession(root);

        var (output, code) = await driver.RunCommandAsync("ls -la \"/\"");

        Assert.Equal(0, code);
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains(lines, line => line.StartsWith("total ", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.StartsWith("-rw-r--r--", StringComparison.Ordinal) && line.EndsWith("readme.txt", StringComparison.Ordinal) && line.Contains(" 5 "));
        Assert.Contains(lines, line => line.StartsWith("drwxr-xr-x", StringComparison.Ordinal) && line.EndsWith("subdir", StringComparison.Ordinal));
        await driver.CloseAsync(session);
    }

    [Fact]
    public async Task MkdirAndCd_CreateAndEnterDirectory()
    {
        var root = CreateRoot();
        var (session, driver) = StartSession(root);

        var (_, mkdirCode) = await driver.RunCommandAsync("mkdir \"/created\"");
        var (_, cdCode) = await driver.RunCommandAsync("cd \"/created\"");
        var (pwdOutput, _) = await driver.RunCommandAsync("pwd");

        Assert.Equal(0, mkdirCode);
        Assert.Equal(0, cdCode);
        Assert.Equal("/created", pwdOutput.Trim());
        Assert.True(Directory.Exists(Path.Combine(root, "created")));
        await driver.CloseAsync(session);
    }

    [Fact]
    public async Task ScpUpload_OverShell_WritesFile_AndReportsSuccess()
    {
        var root = CreateRoot();
        var (session, driver) = StartSession(root);
        var payload = Encoding.UTF8.GetBytes("winscp-shell-upload");

        await driver.SendWrappedAsync("scp -r -d -t \"/\"");
        await driver.ExpectBeginAsync();
        Assert.Equal(0, await driver.ReadByteAsync());               // server ready
        await driver.WriteTextAsync($"C0644 {payload.Length} up.txt\n");
        Assert.Equal(0, await driver.ReadByteAsync());               // ready for data
        await driver.WriteBytesAsync(payload);
        await driver.WriteByteAsync(0);                              // data terminator
        Assert.Equal(0, await driver.ReadByteAsync());               // upload ack
        var code = await driver.ExpectEndAsync();

        Assert.Equal(0, code);
        Assert.Equal("winscp-shell-upload", await File.ReadAllTextAsync(Path.Combine(root, "up.txt")));
        await driver.CloseAsync(session);
    }

    [Fact]
    public async Task ScpDownload_OverShell_SendsFile_AndReportsSuccess()
    {
        var root = CreateRoot();
        await File.WriteAllTextAsync(Path.Combine(root, "down.txt"), "winscp-shell-download");
        var (session, driver) = StartSession(root);

        await driver.SendWrappedAsync("scp -r -d -f \"/down.txt\"");
        await driver.ExpectBeginAsync();
        await driver.WriteByteAsync(0);                              // sink ready
        var header = await driver.ReadLineAsync();
        Assert.StartsWith("C0644 ", header);
        var length = int.Parse(header.Split(' ')[1]);
        await driver.WriteByteAsync(0);                              // ack record
        var data = await driver.ReadExactAsync(length);
        Assert.Equal(0, await driver.ReadByteAsync());               // data terminator
        await driver.WriteByteAsync(0);                              // ack data
        var code = await driver.ExpectEndAsync();

        Assert.Equal(0, code);
        Assert.Equal("winscp-shell-download", Encoding.UTF8.GetString(data));
        await driver.CloseAsync(session);
    }

    [Fact]
    public async Task ScpUpload_DeniedTarget_ReportsFailureReturnCode()
    {
        var root = CreateRoot();
        Directory.CreateDirectory(Path.Combine(root, "readonly"));
        var (session, driver) = StartSession(root);
        var payload = Encoding.UTF8.GetBytes("x");

        // Upload targeting a path whose parent is a file → server rejects with a fatal SCP error.
        await File.WriteAllTextAsync(Path.Combine(root, "afile"), "blocker");
        await driver.SendWrappedAsync("scp -r -d -t \"/afile/child.txt\"");
        await driver.ExpectBeginAsync();
        var firstByte = await driver.ReadByteAsync();
        // Server either sends the ready null then errors, or errors immediately (0x02).
        if (firstByte == 0)
        {
            await driver.WriteTextAsync($"C0644 {payload.Length} child.txt\n");
        }

        var code = await driver.DrainToEndAsync();
        Assert.NotEqual(0, code);
        await driver.CloseAsync(session);
    }

    private sealed class WinScpShellDriver
    {
        private readonly LoopbackStream _toServer;
        private readonly LoopbackStream _fromServer;

        public WinScpShellDriver(LoopbackStream toServer, LoopbackStream fromServer)
        {
            _toServer = toServer;
            _fromServer = fromServer;
        }

        public async Task<(string Output, int Code)> RunCommandAsync(string command)
        {
            await SendWrappedAsync(command);
            await ExpectBeginAsync();
            var output = new StringBuilder();
            while (true)
            {
                var line = await ReadLineAsync();
                if (line.StartsWith(EndMarker, StringComparison.Ordinal))
                {
                    return (output.ToString(), ParseCode(line));
                }

                output.Append(line).Append('\n');
            }
        }

        public Task SendWrappedAsync(string command)
            => WriteTextAsync($"echo \"{BeginMarker}\" ; {command} ; echo \"{EndMarker}$?\"\n");

        public async Task ExpectBeginAsync()
        {
            while (true)
            {
                var line = await ReadLineAsync();
                if (line == BeginMarker)
                {
                    return;
                }
            }
        }

        public async Task<int> ExpectEndAsync()
        {
            while (true)
            {
                var line = await ReadLineAsync();
                if (line.StartsWith(EndMarker, StringComparison.Ordinal))
                {
                    return ParseCode(line);
                }
            }
        }

        /// <summary>Reads whatever remains (ignoring any binary/error bytes) until the end marker.</summary>
        public async Task<int> DrainToEndAsync()
        {
            var buffer = new List<byte>();
            while (true)
            {
                var value = await ReadByteAsync();
                if (value < 0)
                {
                    return -1;
                }

                if (value == (byte)'\n')
                {
                    var line = Encoding.UTF8.GetString(buffer.ToArray()).TrimEnd('\r');
                    buffer.Clear();
                    var index = line.IndexOf(EndMarker, StringComparison.Ordinal);
                    if (index >= 0)
                    {
                        return ParseCode(line[index..]);
                    }
                }
                else
                {
                    buffer.Add((byte)value);
                }
            }
        }

        private static int ParseCode(string endLine)
        {
            var suffix = endLine[EndMarker.Length..].Trim();
            return int.TryParse(suffix, out var code) ? code : -1;
        }

        public async Task<string> ReadLineAsync()
        {
            var bytes = new List<byte>();
            while (true)
            {
                var value = await ReadByteAsync();
                if (value < 0)
                {
                    return Encoding.UTF8.GetString(bytes.ToArray());
                }

                if (value == (byte)'\n')
                {
                    if (bytes.Count > 0 && bytes[^1] == (byte)'\r')
                    {
                        bytes.RemoveAt(bytes.Count - 1);
                    }

                    return Encoding.UTF8.GetString(bytes.ToArray());
                }

                bytes.Add((byte)value);
            }
        }

        public async Task<int> ReadByteAsync()
        {
            var buffer = new byte[1];
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var read = await _fromServer.ReadAsync(buffer.AsMemory(0, 1), timeout.Token);
            return read == 0 ? -1 : buffer[0];
        }

        public async Task<byte[]> ReadExactAsync(int count)
        {
            var buffer = new byte[count];
            var offset = 0;
            while (offset < count)
            {
                var value = await ReadByteAsync();
                if (value < 0)
                {
                    throw new EndOfStreamException();
                }

                buffer[offset++] = (byte)value;
            }

            return buffer;
        }

        public Task WriteTextAsync(string text)
            => WriteBytesAsync(Encoding.UTF8.GetBytes(text));

        public async Task WriteBytesAsync(byte[] bytes)
        {
            await _toServer.WriteAsync(bytes);
            await _toServer.FlushAsync();
        }

        public Task WriteByteAsync(byte value)
            => WriteBytesAsync([value]);

        public async Task CloseAsync(Task session)
        {
            _toServer.Complete();
            await session.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    /// <summary>Unbounded in-memory duplex stream half, mirroring the SSH channel stream behaviour.</summary>
    private sealed class LoopbackStream : Stream
    {
        private readonly Channel<byte[]> _channel = Channel.CreateUnbounded<byte[]>();
        private byte[]? _current;
        private int _offset;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public void Complete() => _channel.Writer.TryComplete();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (_current is null || _offset >= _current.Length)
            {
                if (!await _channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    return 0;
                }

                if (_channel.Reader.TryRead(out var next))
                {
                    _current = next;
                    _offset = 0;
                }
            }

            var length = Math.Min(buffer.Length, _current.Length - _offset);
            _current.AsMemory(_offset, length).CopyTo(buffer);
            _offset += length;
            return length;
        }

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _channel.Writer.TryWrite(buffer.ToArray());
            return ValueTask.CompletedTask;
        }

        public override void Write(byte[] buffer, int offset, int count)
            => _channel.Writer.TryWrite(buffer.AsSpan(offset, count).ToArray());

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
