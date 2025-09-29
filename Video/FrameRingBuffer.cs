namespace Tractus.HtmlToNdi.Video;

/// <summary>
/// Thread-safe drop-oldest ring buffer for buffered video frames.
/// </summary>
public sealed class FrameRingBuffer : IFrameProvider
{
    private readonly BufferedVideoFrame?[] _frames;
    private readonly object _sync = new();
    private int _count;
    private int _head;

    public FrameRingBuffer(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _frames = new BufferedVideoFrame[capacity];
    }

    public int Capacity => _frames.Length;

    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _count;
            }
        }
    }

    /// <summary>
    /// Adds a frame to the buffer, returning any frame that was dropped to make space.
    /// </summary>
    public BufferedVideoFrame? Enqueue(BufferedVideoFrame frame)
    {
        lock (_sync)
        {
            BufferedVideoFrame? dropped = null;
            if (_count == _frames.Length)
            {
                dropped = _frames[_head];
                _frames[_head] = frame;
                _head = (_head + 1) % _frames.Length;
            }
            else
            {
                var tail = (_head + _count) % _frames.Length;
                _frames[tail] = frame;
                _count++;
            }

            return dropped;
        }
    }

    /// <summary>
    /// Takes the most recent frame, discarding any stale frames.
    /// </summary>
    public bool TryTakeLatest(out BufferedVideoFrame frame, out int discarded)
    {
        lock (_sync)
        {
            if (_count == 0)
            {
                frame = null!;
                discarded = 0;
                return false;
            }

            discarded = Math.Max(0, _count - 1);

            // Dispose all but the newest frame.
            for (var i = 0; i < _count - 1; i++)
            {
                var index = (_head + i) % _frames.Length;
                _frames[index]?.Dispose();
                _frames[index] = null;
            }

            var latestIndex = (_head + _count - 1) % _frames.Length;
            frame = _frames[latestIndex]!;
            _frames[latestIndex] = null;
            _count = 0;
            _head = (latestIndex + 1) % _frames.Length;
            return true;
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            for (var i = 0; i < _frames.Length; i++)
            {
                _frames[i]?.Dispose();
                _frames[i] = null;
            }

            _count = 0;
            _head = 0;
        }
    }
}
