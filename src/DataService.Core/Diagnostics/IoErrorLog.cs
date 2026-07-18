using DataService.Core.Events;

namespace DataService.Core.Diagnostics;

public sealed record IoErrorEntry(
    DateTimeOffset Timestamp,
    ProtocolKind? Protocol,
    IoErrorCategory Category,
    string? Path,
    string Message);

/// <summary>
/// Fixed-size in-memory ring buffer for IO errors. Deliberately never touches the
/// file system so it keeps working while the IO subsystem itself is failing.
/// Not persisted by design.
/// </summary>
public sealed class IoErrorLog
{
    private readonly object _lock = new();
    private readonly IoErrorEntry?[] _entries;
    private int _next;
    private int _count;
    private long _totalReported;

    public IoErrorLog(int capacity = 256)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _entries = new IoErrorEntry?[capacity];
    }

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _count;
            }
        }
    }

    public long TotalReported
    {
        get
        {
            lock (_lock)
            {
                return _totalReported;
            }
        }
    }

    public void Report(IoErrorEntry entry)
    {
        lock (_lock)
        {
            _entries[_next] = entry;
            _next = (_next + 1) % _entries.Length;
            _count = Math.Min(_count + 1, _entries.Length);
            _totalReported++;
        }
    }

    /// <summary>Returns the buffered entries, newest first.</summary>
    public IReadOnlyList<IoErrorEntry> Snapshot()
    {
        lock (_lock)
        {
            var result = new List<IoErrorEntry>(_count);
            for (var offset = 1; offset <= _count; offset++)
            {
                var index = (_next - offset + _entries.Length) % _entries.Length;
                if (_entries[index] is { } entry)
                {
                    result.Add(entry);
                }
            }

            return result;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            Array.Clear(_entries);
            _next = 0;
            _count = 0;
        }
    }
}
