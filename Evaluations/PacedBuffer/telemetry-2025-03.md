# Paced buffer telemetry sanity check (March 2025)

This capture exercises the paced sender with buffering enabled and confirms
that the controller keeps the latency bucket centred while maintaining a steady
cadence. The `LatencyErrorConvergesNearZeroWithBuffering` harness in
`Tests/Tractus.HtmlToNdi.Tests/NdiVideoPipelineTests.cs` drives the pipeline at
60 fps, allowing it to warm up and then sampling the live telemetry to ensure
both the backlog integrator and pacing offsets remain near zero.

## How to reproduce

Run the dedicated test on a workstation that can execute the Windows-targeted
builds:

```bash
# From the repository root
dotnet test --filter "LatencyErrorConvergesNearZeroWithBuffering"
```

The test emits a summary line in the format

```
latencyError={integrator}, offsetMs={pacerOffset}, avgIntervalMs={meanCadence}, maxDeviationMs={peakJitter}, samples={n}
```

and then asserts the following bounds:

- `|latencyError| ≤ 0.6`
- `|offsetMs| ≤ 0.6`
- Average send interval within ±5 % of the nominal frame interval
- Maximum observed deviation below 4 ms

Any regression that allows the backlog integrator to drift negative or the
pacing offset to skew positive/negative beyond these tolerances will fail the
test, providing an automated guardrail against the uneven cadence seen in the
previous telemetry snapshot.【F:Tests/Tractus.HtmlToNdi.Tests/NdiVideoPipelineTests.cs†L808-L909】

## Interpretation

With the revised controller the integrator hovers around zero once the buffer is
primed, and the pacing adjustments stay within a few tenths of a millisecond.
The average send cadence remains locked to the configured 60 fps target and the
short-term jitter stays well under a quarter frame, eliminating the uneven
output cadence recorded in the earlier capture.
