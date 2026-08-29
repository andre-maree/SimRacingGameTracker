# TODO: Deferred Work and Future Enhancements

This file tracks work that was deliberately deferred during the initial implementation, plus ideas for future enhancements.

---

## Deferred from the Brief

### 1. Add Refresh Token Flow
**Status:** Not implemented  
**Reason:** The brief asks for 24-hour JWTs. A refresh token flow adds complexity (secure storage of refresh token, rotation policy, revocation endpoint) without demonstrating additional architectural skill beyond what login/JWT issuance already shows.

**Implementation notes for when added:**
- Issue a long-lived refresh token (e.g., 7 days) alongside the access token at login
- Store refresh token in DPAPI alongside the JWT
- Add `POST /api/auth/refresh` endpoint that exchanges a valid refresh token for a new access token
- Rotate the refresh token on each use (detects token theft)
- Add a revocation table to the server database for invalidated tokens

**Estimated effort:** ~half a day

---

## Stretch Goals from the Brief

### 2. Push Direction: Upload Recorded Sessions Back to Server
**Status:** Implemented (Part 2 of the brief marked as `*` stretch goal)

Sessions/stints/laps are uploaded via `POST /api/sessions` with exponential-backoff retry.

### 3. Two-Lap Overlay for Part 5
**Status:** Implemented (step 33)

Lap selection and an `Overlay` toggle live in `Sessions.razor`; `InputTraceChart.razor` renders
both traces on one `RadzenChart`, reusing per-channel colours at lighter stroke weight for the
comparison lap.

**Remaining limitation:** the time axis is normalised by **elapsed percentage**, not by lap
distance. Two laps of different durations therefore align proportionally, which is wrong if one
lap contains an off-track excursion or a spin — the same percentage is no longer the same corner.
Aligning on distance travelled requires capturing `LapDistance` per sample and resampling both
traces onto a shared distance grid.

**Estimated effort:** ~half a day

---

## Known Technical Debt

### 4. Server Cannot Reference `GameTracker.Telemetry`
**Issue:** The telemetry library targets `net10.0-windows` for memory-mapped file access. The server (`net10.0`) can't reference it.

**Impact:** Server-side telemetry replay is impossible without restructuring.

**Proposed fix:**
- Split into `GameTracker.Telemetry` (pure, `net10.0`) — contains `ITelemetrySource`, `TelemetryFrame`, `SessionStateMachine`, `RecordingEvent`
- Create `GameTracker.Telemetry.R3E` (`net10.0-windows`) — contains `SharedMemoryTelemetrySource`, `R3E.cs`, Windows-specific interop
- Server references the pure library, WPF references both

**Estimated effort:** ~2 hours

---

### 5. Input Telemetry Blobs Are Opaque to SQL
**Issue:** Part 5 stores telemetry as quantised, delta-encoded, Brotli-compressed blobs. SQL cannot filter inside a blob (e.g., "laps where throttle exceeded 95% in sector 2").

**Impact:** Analytics queries require loading and decoding laps in application code.

**Proposed fix (if analytics are needed):**
- Store denormalized summary stats alongside the blob (e.g., `MaxThrottle`, `MaxBrake`, `AvgSteering` per lap)
- Compute during compression on the consumer task (zero read-path cost)
- Index these columns for fast filtering

**Estimated effort:** ~3 hours

---

### 6. No Conflict Resolution for Competing Writes
**Issue:** If multiple clients sync from the same server and modify local copies (hypothetically), last-write-wins with no merge.

**Impact:** Rare in this application (clients don't modify the catalogue, only read it), but a true multi-writer sync would need conflict detection.

**Proposed fix:**
- Add `ModifiedByClientId` to each row
- Detect concurrent modifications (same `PrimaryKey`, different `ModifiedByClientId`, overlapping `ServerVersion` range)
- Surface conflicts in the UI and force user resolution

**Estimated effort:** ~1 day

---

### 7. Version Gate Is Strict
**Issue:** If RaceRoom updates the shared memory layout mid-assessment, the app refuses to start rather than attempting a best-effort parse.

**Impact:** Developer has to re-copy `R3E.cs` and rebuild.

**Proposed fix:**
- Maintain a known-good version list instead of a single pair
- Add a "compatibility mode" flag to attempt parsing with warnings if the version is unrecognized but the size matches
- Log every field read with a try/catch to detect struct misalignment

**Estimated effort:** ~half a day

---

### 7a. Input Quantisation Is Lossy
**Issue:** Inputs are quantised to 16 bits before compression, so the stored trace is not
bit-identical to what was captured. Measured maximum round-trip error is `1.53e-5`.

**Impact:** None in practice — this is orders of magnitude below what a pedal or wheel can
resolve, and below the noise floor of the game's own input reporting. It is recorded here only so
the loss is a documented decision rather than a hidden surprise for anyone who later diffs
captured against stored data.

**If bit-exactness is ever required:** store the raw `float32` columns instead and accept ~4x the
storage (measured: 48,126 bytes vs 11,990 bytes for a 90-second lap).

**Estimated effort:** ~1 hour to make the format selectable per-lap

---

## Enhancements Beyond the Brief

### 8. Add a Web-Based Telemetry Viewer
Render recorded sessions and lap traces in the Blazor Server UI, not just the WPF client.

**Requires:**
- Migrating Part 5 rendering code from WPF to `GameTrackerRazorLibrary`
- Adding a `/sessions/{id}` detail page with chart display

**Estimated effort:** ~1 day

---

### 9. Add Push Notifications for Sync Completion
Show a toast notification in the WPF client when a sync completes or fails.

**Requires:**
- Radzen `NotificationService`
- Wiring the `CatalogueSyncService` completion/failure to the UI

**Estimated effort:** ~1 hour

---

### 10. Add Lap Comparison Metrics
For two selected laps, compute and display:
- Delta time at each sector
- Throttle/brake overlap percentage (how similar the inputs were)
- Maximum difference in steering angle

**Requires:**
- Integrating with the two-lap overlay feature (item 3)
- Adding a metrics panel below the chart

**Estimated effort:** ~half a day

---

### 11. Add a "Replay" Endpoint for Server-Side Analysis
Allow uploading a recorded session from the WPF client and "replaying" it through the state machine on the server for validation.

**Requires:**
- Fixing item 4 (split telemetry library into pure + interop)
- Adding a `POST /api/sessions/replay` endpoint that accepts a telemetry stream and returns the computed sessions/stints/laps

**Estimated effort:** ~1 day

---

### 12. Add Live Telemetry Streaming to a Web Dashboard
Stream real-time telemetry from the WPF client to a SignalR hub on the server, viewable in a web browser.

**Requires:**
- Adding SignalR to both server and client
- Rendering live charts in Blazor

**Estimated effort:** ~2 days

---

## Testing

### 13. Add Unit Tests for `SessionStateMachine`
The state machine is designed to be pure and unit-testable. Add tests for:
- Monotonic-time restart guard
- Latched lap validity
- Pit stop stint boundaries
- Quit-to-menu mid-lap

**Estimated effort:** ~half a day

---

### 14. Add Integration Tests for Sync
Test the full sync pipeline:
- Seed server with data
- Run client sync
- Verify local SQLite matches
- Modify server data
- Re-sync
- Verify incremental updates

**Estimated effort:** ~1 day

---

_This TODO is a living document. Items may be promoted to the main plan or closed as "won't fix" as the project evolves._
