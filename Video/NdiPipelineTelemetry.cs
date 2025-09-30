namespace Tractus.HtmlToNdi.Video;

public sealed class NdiPipelineTelemetry
{
    public long CapturedFrames => Interlocked.Read(ref _captured);
    public long SentFrames => Interlocked.Read(ref _sent);
    public long RepeatedFrames => Interlocked.Read(ref _repeated);
    public long DroppedFrames => Interlocked.Read(ref _dropped);
    public long BufferOverflows => Interlocked.Read(ref _bufferOverflow);
    public long Underruns => Interlocked.Read(ref _underruns);

    private long _captured;
    private long _sent;
    private long _repeated;
    private long _dropped;
    private long _bufferOverflow;
    private long _underruns;

    public void IncrementCaptured() => Interlocked.Increment(ref _captured);

    public void IncrementSent() => Interlocked.Increment(ref _sent);

    public void IncrementRepeated() => Interlocked.Increment(ref _repeated);

    public void AddDropped(long count)
    {
        if (count <= 0)
        {
            return;
        }

        Interlocked.Add(ref _dropped, count);
    }

    public void IncrementOverflow() => Interlocked.Increment(ref _bufferOverflow);

    public void IncrementUnderrun() => Interlocked.Increment(ref _underruns);
}
