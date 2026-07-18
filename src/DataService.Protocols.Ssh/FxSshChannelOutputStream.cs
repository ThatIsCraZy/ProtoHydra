using FxSsh.Services;

namespace DataService.Protocols.Ssh;

internal sealed class FxSshChannelOutputStream : Stream
{
    private readonly Channel _channel;

    public FxSshChannelOutputStream(Channel channel)
        => _channel = channel;

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override Task FlushAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;

    public override long Seek(long offset, SeekOrigin origin)
        => throw new NotSupportedException();

    public override void SetLength(long value)
        => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count)
        => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
    {
        var data = new byte[count];
        Buffer.BlockCopy(buffer, offset, data, 0, count);
        _channel.SendData(data);
    }

    public override ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        _channel.SendData(buffer.ToArray());
        return ValueTask.CompletedTask;
    }
}
