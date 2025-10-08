# Fact-check: paced buffer underrun handling in PRs #54–#58

## Statement 1
> Every submission still discards all but the newest capture on underrun, so recovery always consists of several repeated frames while the backlog rebuilds; none of the branches try to keep older frames to trade extra latency for smoother motion.

Each PR transitions into warm-up by calling a helper that removes every queued frame except the most recent capture:

- **PR #54 (`ab83350a`)**
  ```csharp
  ringBuffer.DiscardAllButLatest();
  ```
  — `EnterWarmup` flushes the buffer to one frame before repeating during warm-up.
- **PR #55 (`acceb7ef`)**
  ```csharp
  ringBuffer.DrainToLatestAndKeep();
  ```
  — the underrun branch drains older frames immediately after logging the underrun.
- **PR #56 (`3eeef79f`)**
  ```csharp
  ringBuffer.DropAllButLatest();
  ```
  — both the steady-state and re-priming paths prune to the freshest capture.
- **PR #57 (`fbb6b4b5`)**
  ```csharp
  ringBuffer?.TrimToSingleLatest();
  ```
  — warm-up always trims the queue down to one frame before resuming.
- **PR #58 (`59451d8f`)**
  ```csharp
  ringBuffer.TrimToSingleLatest();
  ```
  — entering warm-up discards every frame except the newest.

Because every branch repeats the last sent frame until the backlog rebuilds, none of these implementations retain extra frames to trade additional latency for smoother motion. The statement is **accurate**.

## Statement 2
> Both PR #55 and PR #58 add helpers that trim to the latest frame without resetting the ring buffer’s overflow bookkeeping, so stale-drop telemetry will remain suppressed after an underrun until enough fresh frames are dequeued.

The newly introduced helpers omit the overflow reset:

- **PR #55 (`FrameRingBuffer.DrainToLatestAndKeep`)**
  ```csharp
  while (frames.Count > 1)
  {
      var stale = frames.Dequeue();
      stale.Dispose();
      if (overflowSinceLastDequeue > 0)
      {
          overflowSinceLastDequeue--;
      }
      else
      {
          DroppedAsStale++;
      }
  }
  ```
  — no assignment to `overflowSinceLastDequeue = 0` after draining.
- **PR #58 (`FrameRingBuffer.TrimToSingleLatest`)**
  ```csharp
  while (frames.Count > 1)
  {
      var stale = frames.Dequeue();
      stale.Dispose();
      if (overflowSinceLastDequeue > 0)
      {
          overflowSinceLastDequeue--;
      }
      else
      {
          DroppedAsStale++;
      }
  }
  return frames.Peek();
  ```
  — also leaves `overflowSinceLastDequeue` untouched.

Without clearing `overflowSinceLastDequeue`, any overflow drop that occurred before the underrun continues to mask subsequent stale drops in telemetry until enough successful dequeues decrement the counter. The statement is **accurate**.
