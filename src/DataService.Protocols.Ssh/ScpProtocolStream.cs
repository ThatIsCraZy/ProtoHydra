using System.Text;

namespace DataService.Protocols.Ssh;

internal sealed class ScpProtocolStream
{
    private const int MaxControlLineLength = 16 * 1024;
    private readonly Stream _input;
    private readonly Stream _output;

    public ScpProtocolStream(Stream input, Stream output)
    {
        _input = input;
        _output = output;
    }

    public async Task ReadOkAsync(CancellationToken cancellationToken)
    {
        var code = await ReadByteAsync(cancellationToken).ConfigureAwait(false);
        if (code == 0)
        {
            return;
        }

        var message = await ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (code == 1)
        {
            // Code 1 is a non-fatal warning per SCP convention; only code 2 aborts the transfer.
            return;
        }

        throw new IOException($"SCP peer returned {code}: {message}");
    }

    public async Task WriteOkAsync(CancellationToken cancellationToken)
    {
        await _output.WriteAsync(new byte[] { 0 }, cancellationToken).ConfigureAwait(false);
        await _output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteErrorAsync(string message, bool fatal, CancellationToken cancellationToken)
    {
        var data = Encoding.UTF8.GetBytes($"{(fatal ? '\x02' : '\x01')}scp: {message}\n");
        await _output.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        await _output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteLineAsync(string line, CancellationToken cancellationToken)
    {
        var data = Encoding.UTF8.GetBytes(line + "\n");
        await _output.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        await _output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> ReadControlLineAsync(CancellationToken cancellationToken)
    {
        var first = await ReadByteAsync(cancellationToken).ConfigureAwait(false);
        while (first == 1)
        {
            // Non-fatal warning from the peer; skip its message and read the next record.
            await ReadLineAsync(cancellationToken).ConfigureAwait(false);
            first = await ReadByteAsync(cancellationToken).ConfigureAwait(false);
        }

        if (first == 2)
        {
            var error = await ReadLineAsync(cancellationToken).ConfigureAwait(false);
            throw new IOException($"SCP peer error {first}: {error}");
        }

        var line = new List<byte> { first };
        while (line.Count < MaxControlLineLength)
        {
            var next = await ReadByteAsync(cancellationToken).ConfigureAwait(false);
            if (next == (byte)'\n')
            {
                return Encoding.UTF8.GetString([.. line]);
            }

            if (next == (byte)'\r')
            {
                throw new InvalidOperationException("CR is not allowed in SCP control lines.");
            }

            line.Add(next);
        }

        throw new InvalidOperationException("SCP control line is too long.");
    }

    public async Task CopyExactAsync(
        Stream output,
        long length,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[128 * 1024];
        var remaining = length;
        while (remaining > 0)
        {
            var readLength = (int)Math.Min(buffer.Length, remaining);
            var read = await _input.ReadAsync(buffer.AsMemory(0, readLength), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("Unexpected EOF during SCP file data.");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            remaining -= read;
        }
    }

    public async Task CopyFileToRemoteAsync(
        Stream input,
        long length,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[128 * 1024];
        var remaining = length;
        while (remaining > 0)
        {
            var readLength = (int)Math.Min(buffer.Length, remaining);
            var read = await input.ReadAsync(buffer.AsMemory(0, readLength), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("Unexpected EOF from local file.");
            }

            await _output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            remaining -= read;
        }

        await _output.WriteAsync(new byte[] { 0 }, cancellationToken).ConfigureAwait(false);
        await _output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> ReadLineAsync(CancellationToken cancellationToken)
    {
        var line = new List<byte>();
        while (line.Count < MaxControlLineLength)
        {
            var next = await ReadByteAsync(cancellationToken).ConfigureAwait(false);
            if (next == (byte)'\n')
            {
                return Encoding.UTF8.GetString([.. line]);
            }

            line.Add(next);
        }

        throw new InvalidOperationException("SCP line is too long.");
    }

    private async Task<byte> ReadByteAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[1];
        var read = await _input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (read != 1)
        {
            throw new EndOfStreamException("SCP connection closed.");
        }

        return buffer[0];
    }
}
