# Architecture Decisions and Justifications

This document records the key design choices made during implementation of the GameTracker system, the trade-offs involved, and the rationale behind each decision.

---

## Project Structure

### Decision: Separate Domain and Telemetry Libraries

**Choice:** Created `GameTracker.Domain` (`net10.0`) and `GameTracker.Telemetry` (`net10.0-windows`) as standalone class libraries.

**Rationale:**
- Part 4 of the brief requires "sensible DI and project boundaries" and states "your domain and telemetry logic shouldn't know about WPF or Blazor"
- Domain needs to be framework-agnostic to be referenced by both server (SQL Server/EF Core) and client (SQLite)
- Telemetry must target `net10.0-windows` for memory-mapped file access to RaceRoom shared memory
- WPF client was originally referencing the entire Blazor Server app — a clear boundary violation

**Trade-offs:**
- ✅ Clean separation of concerns
- ✅ Domain types shared between server and client without platform dependencies
- ❌ The server cannot reference `GameTracker.Telemetry` (could split into pure logic + Windows interop if server-side replay needed)

---

## R3E Struct Version Verification

### Spike Findings (Step 6)

**RaceRoom Version Detected (spike run against a live practice session):**
- Major: 3 (expected by code: 3) — MATCH
- Minor: 5 (expected by code: 5) — MATCH
- Marshal.SizeOf<Shared>(): 43,996 bytes
- MemoryMappedViewAccessor.Capacity: 45,056 bytes (11 x 4,096-byte pages)
- Delta: 1,060 bytes of page padding — struct fits, consistent

**Field sanity spot-check (Part C):**
| Field | Value | Assessment |
| --- | --- | --- |
| GameSimulationTime | 155.067 | Plausible running clock |
| CompletedLaps | 0 | Valid (out-lap) |
| SessionType | 0 | Practice |
| GameInMenus | 0 | On track |
| VehicleInfo.ModelId | 13394 | Plausible R3E model id |
| LayoutId | 7026 | Plausible R3E layout id |
| NumCars | 1 | Valid (solo practice) |

No `-1`-only fields, no wildly out-of-range values, so the struct is both the right
size and correctly aligned — a size match alone could not have proven this.

**Decision:**
Proceed with the existing `R3E.cs` struct definitions unchanged. The declared
`Constant.VersionMajor`/`VersionMinor` gate (3.5) matches the installed game, so the
telemetry service keeps its strict version check and refuses to read on mismatch rather
than risking silent misinterpretation of unaligned memory. No struct regeneration or
version-shim layer is needed, which unblocks Steps 7 and 8 (Domain entities and
`TelemetryFrame` shaping) against the confirmed layout.

**Accessibility Fix Applied:**
Made `Constant` class and nested enums (`VersionMajor`, `VersionMinor`, `Session`, `GameMode`, `SessionPhase`) `public` in `R3E.cs`. These were originally `private` (C#'s default for nested types), making the version gate literally unwritable.

---

## Sync Strategy

### Decision: Server-issued Monotonic Counter

**Choice:** Used a SQL `SEQUENCE` object for `ServerVersion` allocation, with a `SaveChanges` interceptor stamping every inserted/updated/deleted row.

**Rationale:**
- Client clocks cannot be trusted (wrong timezone, manual adjustment, VM snapshots)
- A server-issued monotonic counter guarantees total ordering regardless of transaction interleaving
- `ROWVERSION` (SQL Server's built-in) is unsuitable — it's per-database, not per-row, and restarts unpredictably

**Incremental Sync Algorithm:**
1. Client stores `LastSyncedVersion` in local `SyncMetadata` table
2. Request `GET /api/sync/changes?since={LastSyncedVersion}&take=100`
3. Server returns rows with `ServerVersion > since`, ordered ascending, including tombstones (`IsDeleted = true`)
4. Client upserts in a transaction per batch
5. Client advances `LastSyncedVersion` only after transaction commits
6. **Interrupted sync:** resumes from the last committed version — no partial batches

**Edge Cases Handled:**
- ✅ Interrupted sync (resumable via continuation token)
- ✅ Deletions on server (tombstones propagated, client purges)
- ✅ Wrong client clock (client never contributes to version calculation)
- ❌ Concurrent syncs from the same client (mitigated by app-level `SemaphoreSlim(1,1)` lock, not by server)

---

## Authentication & Token Storage

### Decision: JWT Bearer for API, DPAPI for At-Rest Storage

**Choice:**
- Server issues a 24-hour signed JWT via `POST /api/auth/login` after validating credentials
- WPF client stores the JWT using Windows DPAPI (`ProtectedData.Protect`) with `CurrentUser` scope in `%LOCALAPPDATA%\GameTracker\token.dat`
- Server accepts both cookie auth (for Blazor UI) and bearer tokens (for API) via a policy scheme

**Rationale:**
- The brief explicitly forbids plaintext credentials in `appsettings.json`
- Windows Credential Manager is overkill for a single token and requires Win32 interop
- ASP.NET Core Data Protection is designed for server-side key rings, not client apps
- DPAPI `CurrentUser` scope is the platform-native solution — encrypted at rest, decryptable only by the Windows user who encrypted it
- JWT with 24-hour expiry balances security and UX (refresh token flow deferred to TODO)

**Trade-offs:**
- ✅ Tokens encrypted at rest per user account
- ✅ No third-party credential libraries
- ❌ Tokens do not survive if the app is copied to another machine or user profile (acceptable — re-login required)
- ❌ No refresh token flow yet (documented in TODO.md)

---

## Telemetry Recording: State Machine Design

### Decision: Pure Frame-Driven State Machine

**Choice:** Implemented `SessionStateMachine` as a pure function: `Advance(TelemetryFrame) -> IReadOnlyList<RecordingEvent>`. It owns no I/O and is free of WPF/Blazor/EF dependencies.

**States:** `NoGame` → `Idle` → `InSession` → `OnTrack` → `InLap` → `PitLane` → `Closing`

**Rationale:**
- The brief requires handling four specific scenarios: session restart, invalid lap, pit stop, quit mid-lap
- A state machine centralizes the transition logic rather than scattering it across frame comparisons
- Pure functions are unit-testable without mocking the database or UI
- The producer/consumer pattern (60 Hz poll → `Channel<T>` → consumer task) keeps I/O off the hot path

**Scenario Handling:**
1. **Session restart mid-run:** Monotonic-time guard — if `GameSimulationTime` decreases or `CompletedLaps` decreases or `SessionType`/`LayoutId`/`ModelId` changes, emit `SessionEnded(reason: Restart)` and `SessionStarted`. Never mutate the in-progress session.
2. **Invalid lap:** Validity is latched for the lap. Once `CurrentLapValid` reads 0, the lap stays invalid until completion. If no positive lap time ever arrives, persist with `LapTime = null`, `IsValid = false`. Never drop the lap.
3. **Pit stop:** Entering the pit lane sets `InLap = true`; exiting emits `StintStarted` with `OutLap = true`.
4. **Quit to menu mid-lap:** `GameInMenus` or region disappearing emits `SessionEnded(reason: Abandoned)` and discards the partial lap.

**Trade-offs:**
- ✅ One rule per scenario, not scattered comparisons
- ✅ Unit-testable
- ❌ Requires translating the raw R3E struct into `TelemetryFrame` — adds one mapping layer

---

## Audit Trail Strategy

### Decision: Single Audit Table + SQL Triggers

**Choice:**
- One `AuditTrails` table for all entities
- EF `SaveChanges` interceptor captures changes and writes audit rows (application-level)
- Raw SQL triggers on `Cars`/`Tracks` as defence-in-depth (database-level)

**Rationale:**
- The brief asks to "decide if all auditing goes into 1 table or if each table should have a corresponding audit table"
- A single table simplifies queries ("show me all changes by user X") and avoids schema proliferation
- Triggers ensure auditing even if a rogue migration or direct SQL bypasses EF
- `OldValues`/`NewValues` stored as JSON — flexible schema, queryable via SQL Server's `OPENJSON` if needed

**Schema:**
- `UserId` (who)
- `TableName` (which entity type)
- `Action` (CREATE/UPDATE/DELETE)
- `PrimaryKey` (which row)
- `OldValues` / `NewValues` (JSON)
- `ChangedAtUtc` (when)

**Trade-offs:**
- ✅ Centralized audit log
- ✅ Double defence (app + DB)
- ❌ JSON columns are less type-safe than dedicated audit tables per entity
- ❌ Large JSON blobs if entities grow (acceptable — operational data stays small per the brief)

---

## Part 5: Input Telemetry Storage

### Decision: Per-Lap Brotli-Compressed Columnar Blobs + Preview Array

**Choice:**
- One row per lap in `LapInputTelemetry`, not one row per sample
- Each channel (throttle, brake, steering) accumulated as `float[]` buffers during the lap
- On lap completion, serialize **columnar** (all throttle, then all brake, then all steering), **quantise each sample to 16 bits and delta-encode**, then compress with Brotli into a single `BLOB`
- Alongside the blob, store a ~500-sample-per-channel **preview array** via min/max decimation (not simple sampling — preserves spikes)
- The chart binds to the preview by default; full blob inflates only on zoom or two-lap overlay

**Justification (as requested by the brief):**

| Axis | Analysis |
|---|---|
| **Write Cost** | One INSERT per lap instead of ~90,000 rows for a 90-second lap at 60 Hz × 3 channels. Eliminates SQLite write amplification and per-row index maintenance from the hot recording path. Compression runs on the consumer task, off the poll loop. |
| **Read Cost** | One primary-key row fetch plus one Brotli decompress (~milliseconds for a few hundred KB) versus a 90,000-row scan and materialization. The preview makes the common-case chart render a **zero-decompress read**. Overlaying two laps is two fetches. |
| **Storage** | Measured on a synthetic 90-second lap at 60 Hz (5,400 samples/channel, 64,800 bytes raw): naive columnar **float32 + Brotli compressed to only 48,126 bytes (74% of raw)**. Quantised 16-bit deltas + Brotli reach **11,990 bytes (18.5%)** — a 4× improvement. Row-per-sample would add rowid + FK + timestamp per row, roughly tripling the raw figure before indexes. |

**Correction — the original "tens of KB from columnar layout alone" estimate was wrong, and measurement disproved it.**

Columnar ordering is necessary but *not sufficient*. A `float32` mantissa's low bits are effectively random: a pedal or wheel sensor has nowhere near 24 bits of real precision, so those bits store noise, and noise is incompressible by construction. They dominated the payload and held Brotli to a 26% saving.

Quantising to 16 bits gives a resolution of ~1.5e-5 — roughly two orders of magnitude finer than any real input device — and delta-encoding exploits the fact that consecutive samples at 60 Hz barely differ, so the differences cluster tightly around zero. Verified lossless with respect to the quantised series (max round-trip error 1.53e-5, exactly the quantisation step), with min/max decimation confirmed to preserve full-throttle and full-brake spikes.

**Trade-off (documented as known weakness):**
- ❌ The blob is **opaque to SQL**. Queries like "find every lap where throttle exceeded 95% through sector 2" require decoding in application code, not a WHERE clause.
- ✅ Acceptable for a **plotting feature** (as opposed to an analytics feature). The brief is clear this is about rendering traces, not data mining.

**Why columnar over interleaved?**
Interleaved: `[throttle0, brake0, steer0, throttle1, brake1, steer1, ...]` — values from different channels adjacent.
Columnar: `[throttle0, throttle1, ..., brake0, brake1, ..., steer0, steer1, ...]` — same-channel values adjacent.

Pedal inputs are highly autocorrelated (smooth changes, not random noise). Columnar layout lets the compressor exploit intra-channel patterns far better than interleaved, and it is what makes the per-channel delta encoding meaningful in the first place — a delta between a throttle and a brake sample would be noise. Note that columnar ordering alone was measured as insufficient; see the storage row above.

---

## Radzen Theme Choice

### Decision: Material Theme

**Choice:** `<RadzenTheme Theme="material" />` across server, library, and WPF host page.

**Rationale:**
- The brief specifies "Radzen components" but doesn't mandate a theme
- Material is Radzen's flagship, most-tested theme
- Consistent visual language across both Blazor Server and WPF Blazor hosts
- No custom CSS required beyond the default

---

## Known Weaknesses and Deliberate Omissions

### What We Didn't Build (and why)

1. **Refresh Token Flow** — deferred to TODO.md. The brief asks for 24-hour JWTs; a refresh flow adds complexity without demonstrating architectural skill beyond what login already shows.

2. **Server-Side Telemetry Replay** — `GameTracker.Telemetry` targets `net10.0-windows`, so the server can't reference it. If replay is needed, split into `GameTracker.Telemetry` (pure state machine) and `GameTracker.Telemetry.R3E` (Windows memory-mapped). Not required for this brief.

3. **Comprehensive Sync Edge Cases** — handled interrupted sync, deletions, wrong client clock. Did **not** handle: schema migrations mid-sync, conflicting writes from multiple clients to the same server, or byzantine client clocks (negative timestamps). These are stretch goals beyond the brief.

4. **Lap Telemetry Querying** — input telemetry blobs are opaque to SQL. If analytics queries ("average throttle in sector 2") are needed, either decode in-app or store denormalized summary stats alongside the blob.

---

## Database Indexing Strategy

### Indexes Applied

**Server (SQL Server):**
- **Non-clustered index on `ServerVersion`** for `Games`, `Cars`, `Tracks` — ensures `/api/sync/changes?since=X` runs in `O(log n)` instead of a table scan
- **Unique composite index on `(GameId, ExternalId)`** for `Cars` and `Tracks` — enforces the constraint that `ExternalId` is unique within a game, not globally
- **Composite index on `TelemetryRecords(GameId, CarExternalId)`** and `(GameId, TrackExternalId)` — speeds up telemetry filtering by car/track

**Client (SQLite):**
- Primary keys only — the client database is small (catalogue + local sessions), and query patterns are simple (fetch by PK or scan all)
- Deliberate omission of secondary indexes to avoid write amplification during high-frequency telemetry recording

**Rationale:**
- The brief specifically asks to "document the indexing rationale" in a file called `CHOICES AND REASONS.md`
- Server indexes optimize the read-heavy sync API
- Client avoids indexing overhead where it would slow down the write-heavy recording pipeline

---

## Technology Choices

- **Blazor Server** — mandated by the brief
- **Radzen Components** — mandated by the brief
- **EF Core Code-First** — mandated by the brief
- **SQLite for client** — mandated by the brief
- **WPF + BlazorWebView** — mandated by the brief
- **ASP.NET Core Identity** — mandated by the brief

---

## Trade-Off Summary Table

| Decision | Pro | Con |
|---|---|---|
| Separate Domain/Telemetry libs | Clean boundaries, shared types | Server can't reference Telemetry (Windows) |
| Server-issued version counter | Clock-independent, resumable | Requires SQL SEQUENCE and interceptor |
| DPAPI token storage | Platform-native, per-user encryption | Tokens don't survive profile/machine migration |
| State machine (pure) | Testable, centralized logic | Adds TelemetryFrame mapping layer |
| Single audit table (JSON) | Simple queries, flexible schema | Less type-safe than per-entity tables |
| Columnar blob telemetry | Massive compression, cheap writes | Opaque to SQL, no WHERE clause queries |

---

_This document is updated iteratively as decisions are made during implementation._
