# Paced output buffer

The paced NDI pipeline treats the ring buffer as a bucket that must fill before
any buffered frame is transmitted. When buffering is enabled (`--buffer-depth`
> 0), captured frames are copied into the FIFO ring and the pacing loop sleeps
until `BufferDepth` frames are available. Only after the bucket is full does the
loop begin sending frames at the configured cadence, introducing an intentional
latency of roughly `BufferDepth / fps` seconds between capture and broadcast.

Once primed, frames are consumed in FIFO order so receivers experience a stable
presentation delay. If the capture side outruns the sender the oldest frames are
still dropped at enqueue time, but the pacing loop now surfaces the backlog via
telemetry (`buffered=<count>`). When the bucket drains unexpectedly the loop
repeats the last frame, increments an `underruns` counter, and requires the queue
to fill back to the configured depth before resuming normal sends. That
re-warming behaviour keeps presentation latency predictable after a stall.

Telemetry entries now report whether the bucket is `primed`, the number of
queued frames (`buffered`), overflow and stale-drop counts, the running
`underruns` total, and how long the last warm-up took. Watch those metrics to
spot underrun storms or mis-sized buffers.

Because the sender waits for the bucket to fill, the control plane should call
out the added latency. The CLI already exposes `--buffer-depth` and `--fps`;
remind operators that enabling the buffer introduces `BufferDepth / fps` seconds
of delay before the first frame is published and after any underrun.
