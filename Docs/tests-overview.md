# Automated Test Reference

This document explains what each test in the `Tractus.HtmlToNdi.Tests` suite verifies so large language models (and humans!) can
quickly understand coverage expectations. File and method names match the source exactly so you can jump straight to the
implementation when needed.

## `CefWrapperInputValidationTests.cs`
- `SetUrl_DoesNotThrow_WhenUrlIsNull`: Confirms `CefWrapper.SetUrl` ignores `null` inputs without clearing the last non-empty URL.
- `SetUrl_DoesNotThrow_WhenUrlIsWhitespace`: Verifies whitespace URLs are ignored while preserving the current target.
- `SendKeystrokes_DoesNotThrow_WhenModelIsNull`: Ensures `CefWrapper.SendKeystrokes` tolerates a missing payload object.
- `SendKeystrokes_DoesNotThrow_WhenPayloadIsEmpty`: Checks that an empty keystroke payload is treated as a no-op.

## `FrameRateTests.cs`
- `ParseRecognisesBroadcastRates` (theory): Validates `FrameRate.Parse` accepts common decimal and rational broadcast rates.
- `FromDoubleProducesReasonableFraction`: Confirms `FrameRate.FromDouble` approximates arbitrary doubles with a bounded denominator.

## `FramePumpTests.cs`
- `OnDemandRequestsInvokeInvalidation`: Verifies `FramePump.RequestInvalidateAsync` triggers the provided invalidation delegate.
- `PausedPumpQueuesRequestsUntilResumed`: Ensures queued invalidations remain pending while the pump is paused, then flush on resume.
- `WatchdogTriggersInvalidateAfterIdle`: Checks that the watchdog fires when Chromium paints stall.
- `CadenceAlignmentDelaysOnDemandRequests`: Confirms cadence alignment delays demand-based invalidations as configured.
- `WatchdogRemainsIdleWhenPaintsArrive`: Ensures the watchdog stays silent when regular paints arrive.

## `FrameRingBufferTests.cs`
- `DropsOldestWhenCapacityReached`: Validates overflow drops the oldest entry and tracks the overflow counter.
- `DequeueLatestDropsStaleFrames`: Ensures `DequeueLatest` disposes stale frames and counts them as dropped.
- `TryDequeueReturnsOldestWithoutDisposal`: Confirms `TryDequeue` surfaces frames without disposing them.
- `TryDequeueReturnsFalseWhenEmpty`: Asserts the buffer reports emptiness correctly.
- `TrimToSingleLatestResetsOverflowCounter`: Checks trimming to the latest frame clears stale entries and resets counters.

## `NdiVideoPipelineTests.cs`
- `DirectModeSendsImmediately`: Direct-send mode issues a frame with the configured cadence without buffering.
- `BufferedModeWaitsForWarmupBeforeSending`: Buffered mode delays transmission until the warmup depth is reached.
- `BufferedModeRepeatsLastFrameWhenIdle`: Ensures idle buffered mode repeats the last sent frame.
- `BufferedModeRewarmsAfterUnderrun`: Verifies the buffer re-primes after an underrun event.
- `BufferedPacedInvalidationMaintainsDemand`: Confirms paced invalidation keeps exactly one pending demand ticket while primed.
- `BufferedPacedInvalidationDropsFramesWithoutScheduler`: Validates spurious capture tracking when paced invalidation runs without a scheduler.
- `BufferedCaptureRequestsFollowUpInvalidation`: Ensures each captured frame schedules the next invalidation request.
- `LatencyExpansionPlaysQueuedFramesBeforeRepeats`: Checks latency expansion flushes queued frames before repeating content.
- `LatencyExpansionExitsAfterBacklogRecovers`: Ensures latency expansion deactivates after the backlog stabilises.
- `PacedInvalidationRequestsStayBounded`: Verifies paced invalidation never outgrows the configured demand window.
- `PendingInvalidationsClampWhenSchedulerStalls`: Confirms pending invalidations clamp while the scheduler is paused.
- `BufferedInvalidationsRecoverAfterDroppedPaint`: Ensures buffered mode recovers demand tickets after a dropped paint timeout.
- `DirectInvalidationsRecoverAfterDroppedPaint`: Same recovery check as above but for direct-send mode.
- `PacedInvalidationRequestsInDirectMode`: Confirms direct mode still issues paced invalidation requests when enabled.
- `CaptureBackpressurePausesAndResumes`: Verifies the capture gate pauses and resumes Chromium invalidations based on backlog.
- `CaptureBackpressureRequiresPacedInvalidation`: Ensures backpressure only activates when paced invalidation is enabled.
- `LatencyExpansionPreservesBufferedFramesDuringBacklogDrop`: Checks that latency expansion serves preserved frames after a backlog drop.
- `BufferedModeDropsFramesWhenAhead`: Observes the internal `latencyResyncDrops` counter to confirm oversupply trimming.
- `CalculateNextDeadlineDelaysOrHastensBasedOnBacklog`: Exercises the private `CalculateNextDeadline` helper to inspect pacing adjustments.
- `TrySendBufferedFrameMaintainsIntegratorSign`: Uses reflection to ensure the internal integrator preserves its sign when retransmitting.
- `LatencyErrorConvergesNearZeroWithBuffering`: Reads pacing telemetry fields to confirm the integral term converges near zero over time.
- `BufferedModeTracksRepeatedFramesDuringStalls`: Checks the private `repeatedFrames` counter while the sender repeats frames during stalls.
