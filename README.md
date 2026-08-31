# GameTracker

A Blazor Server admin/catalogue application and a Blazor-on-WPF desktop client for recording sim racing telemetry from RaceRoom Racing Experience.

---

## Overview

This project demonstrates:
- **Blazor Server** catalogue management with Radzen components and EF Core
- **ASP.NET Core Identity** with dual authentication (cookies for UI, JWT bearer for API)
- **Incremental sync** from server to desktop client using a server-issued monotonic version cursor
- **Real-time telemetry recording** from RaceRoom Racing Experience shared memory
- **State machine design** for handling session/stint/lap lifecycle and edge cases
- **Producer/consumer pipeline** with `Channel<T>` for high-frequency data capture
- **Compressed input telemetry storage** using quantised, delta-encoded, Brotli-compressed columnar blobs

---

## Prerequisites

### Required
- **.NET 10 SDK** (or later)
- **SQL Server** (LocalDB is fine for development)
- **RaceRoom Racing Experience** (free on Steam) — required for Part 3 (telemetry recording)
  - Ensure shared memory is enabled (it is by default)
  - The game must be running and in a session (not menus) for telemetry to be available

### Optional (for development)
- **Visual Studio 2026** or **Visual Studio Code** with C# Dev Kit
- **SQL Server Management Studio** (for inspecting the database)

---

## How to Run from a Clean Checkout

### 1. Clone the repository
```powershell
git clone <repository-url>
cd GameTrackerSolution
```

### 2. Restore packages
```powershell
dotnet restore
```

### 3. Set the admin password (required)

No credential is committed to this repository, and the seeder **will not invent a default**. If
you skip this step the catalogue still seeds, but no admin account is created and you will not be
able to log in.

Set this **before first running the server**, since seeding happens at application startup.

```powershell
cd GameTrackerBlazorServerApp
dotnet user-secrets set "Seed:AdminPassword" "Your-Strong-Password-1!"
cd ..
```

The password must satisfy the ASP.NET Core Identity default policy (8+ characters, upper, lower,
digit, and non-alphanumeric). The admin email defaults to `admin@gametracker.local` and can be
overridden with `Seed:AdminEmail`.

### 4. Set the JWT signing key (required)

The server signs API tokens with `Jwt:Key`, and **refuses to start without it**. No key is
committed to this repository: a leaked signing key would let anyone mint a valid Admin token.

Run this once per machine. The value is stored outside the repository and persists across
restarts, rebuilds, and branch switches, so it does not need to be set again before each run.

```powershell
cd GameTrackerBlazorServerApp
$bytes = New-Object byte[] 64
[System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
dotnet user-secrets set "Jwt:Key" ([Convert]::ToBase64String($bytes))
cd ..
```

The key must be at least 32 bytes; startup fails fast if it is shorter, because a short key can
be brute-forced to forge tokens.

> **Keep the key stable.** Every issued token is validated against it, so replacing the key
> immediately invalidates every client session. Generate it once per environment and store it.

> **In production**, supply the key through the environment or a key vault instead
> (`Jwt__Key` — a double underscore, since `:` is not portable in environment variable names).
> `dotnet user-secrets` is a development-only provider: it reads the developer's user profile,
> is not part of the published output, and will silently have no effect on a server. If you run
> more than one server instance, they must all share the same key, or a token issued by one is
> rejected by another.
>
> See [Docs/AZURE DEPLOYMENT.md](Docs/AZURE%20DEPLOYMENT.md) for the Azure Key Vault setup.

### 5. Set up the server database

No manual migration step is required. The server calls `DbSeeder.SeedAsync` at startup, which
migrates the database and then seeds it. Simply running the server (step 7) will:

- Create the SQL Server database (connection string in `appsettings.json`)
- Run migrations (schema + audit triggers)
- Seed the catalogue from `Data/r3e-data.json`
- Create `Admin` and `User` roles
- Create the admin account using the password you set in step 3
If you prefer to apply migrations ahead of time, you can still run:

```powershell
cd GameTrackerBlazorServerApp
dotnet ef database update
cd ..
```

Note that this applies **schema only** — seeding happens at application startup, not here.

### 6. Set up the client database
The WPF client database is created automatically on first launch under:
```
%LOCALAPPDATA%\GameTracker\gametracker.db
```

No manual migration step is required.

### 7. Run the Blazor Server app
```powershell
cd GameTrackerBlazorServerApp
dotnet run
```

The app will start on `https://localhost:7157` (HTTP fallback: `http://localhost:5092`) as
configured in `Properties/launchSettings.json`. Navigate there and log in with the admin account
from step 3.

> The WPF client targets `https://localhost:7157/` via `Server:BaseAddress` in
> `GameTrackerWpfClientApp/appsettings.json`. If you change the server's port, change that value
> too or the client will fail to sync.

### 8. Run the WPF Client app
```powershell
cd GameTrackerWpfClientApp
dotnet run
```

On first launch:
1. Log in using credentials from the server
2. The client will sync the catalogue (Games/Cars/Tracks) into local SQLite
3. The client is now usable offline

### 9. Record telemetry (optional)
1. Launch RaceRoom Racing Experience
2. Enter a practice session with any car/track
3. Leave the WPF client running in the background
4. Drive — telemetry is recorded automatically and uploaded in batches to the server

---

## Project Structure

```
GameTrackerSolution/
├── GameTracker.Domain/                  # Domain entities, enums, DTOs (net10.0)
├── GameTracker.Telemetry/               # Telemetry abstractions, R3E interop, state machine (net10.0-windows)
├── GameTrackerBlazorServerApp/          # Blazor Server app + Identity + API
├── GameTrackerWpfClientApp/             # WPF app hosting Blazor via BlazorWebView
├── GameTrackerRazorLibrary/             # Shared Razor components
├── GameTrackerTestingConsoleApp/        # Throwaway spike console (used in step 2/6 of plan)
└── Docs/
	├── PLAN.md                          # Living implementation checklist
	├── TODO.md                          # Deferred work and known technical debt
	├── TESTPLAN.md                      # Clean-machine verification walkthrough
	├── AZURE DEPLOYMENT.md              # JWT signing key and Key Vault setup
	└── CHOICES AND REASONS.md           # Architecture decisions and justifications
```

---

## User Roles

- **Admin** — full CRUD on Games, Cars, Tracks
- **User** — read-only access to catalogue

The seeder creates an admin account **only if** `Seed:AdminPassword` is configured (see step 3 of
the setup instructions). No credentials are committed to this repository, and `DbSeeder.cs`
deliberately skips creation rather than falling back to a guessable default.

---

## API Endpoints

### Authentication
- `POST /api/auth/login` — returns a 24-hour JWT (body: `{ "email": "...", "password": "..." }`)
- `POST /identity/register` — Identity default (via `MapIdentityApi`, mapped under the `/identity` group)
- `POST /identity/login` — Identity default (bearer-token scheme; the WPF client uses `/api/auth/login` instead)

The login endpoint returns an undifferentiated `401` for both an unknown account and a bad
password (account enumeration would otherwise be trivial), and `423` when Identity has locked
the account out.

#### Proving the auth path by hand

The desktop client signs in through the **Sign in** button in the toolbar, which posts to the
same endpoint. To verify the server half independently of the UI:

```powershell
$body = @{ email = "admin@gametracker.local"; password = "<your seeded password>" } | ConvertTo-Json
$token = (Invoke-RestMethod -Uri "https://localhost:7157/api/auth/login" `
    -Method Post -Body $body -ContentType "application/json").accessToken

Invoke-RestMethod -Uri "https://localhost:7157/api/sync/changes?since=0&take=5" `
    -Headers @{ Authorization = "Bearer $token" }
```

A populated `accessToken` plus a successful sync response confirms the token pipeline end to
end. The same call without the `Authorization` header should return `401`.

### Sync
- `GET /api/sync/changes?since={version}&take={count}` — incremental catalogue sync

### Telemetry
- `POST /api/sessions` — upload recorded sessions/stints/laps
- `POST /api/telemetry/batch` — upload batched high-frequency telemetry rows

All telemetry endpoints require a valid JWT bearer token in the `Authorization` header.

---

## Architecture Highlights

### Sync Strategy
- The server allocates a strictly-increasing `ServerVersion` (from a SQL `SEQUENCE`) on every insert/update/delete
- The client stores `LastSyncedVersion` and requests `?since={LastSyncedVersion}`
- Interrupted syncs resume exactly where they left off (no partial batches)
- Deletions propagate as tombstones (`IsDeleted = true`), which the client purges

#### The change cursor, and why the client clock is irrelevant
The cursor is the server-issued `ServerVersion`, **never a timestamp**. Client clocks are wrong
often enough — timezones, manual changes, VM snapshots, DST — that a time-based cursor silently
drops rows: a client whose clock runs fast asks for changes since a future moment and never sees
the rows written in between. Because the version comes from a single server-side sequence and the
client only ever echoes back a number the server gave it, a wrong client clock cannot skip, reorder
or duplicate a row. The client's clock is written to `SyncMetadata.LastSyncedAtUtc` for display
only; nothing in the protocol reads it.

`GET /api/sync/changes` pages Games, Cars and Tracks against that one shared cursor. Each table is
paged independently and then trimmed to the **lowest** version any truncated table reached, so the
published `NextVersion` can never run ahead of rows another table still owes the client. Publishing
a higher cursor would skip those rows permanently, since the server only ever returns rows *above*
the cursor.

#### Deletions on the server
Rows are never hard-deleted server-side. A delete stamps `IsDeleted = true` and allocates a fresh
`ServerVersion`, so the deletion travels the same ordered path as any other change. The client
applies a tombstone by removing the local row outright (`CatalogueSyncService`), keeping the local
mirror small; the server retains the tombstone so a client that has been offline for months still
learns about it. Without this, a delete that happened between two syncs would be invisible — the
row simply wouldn't appear in any page, and the client would keep it forever.

#### Interrupted sync partway through
Each page is applied in **its own transaction, and the cursor is advanced inside that same
transaction**. That ordering is the whole correctness argument: if the process is killed, the
network drops, or the server goes away mid-run, the committed cursor still points at the last
*fully applied* page. The next run re-requests the interrupted page rather than skipping it.
Advancing the cursor before applying rows would lose them forever. Pages already applied before the
interruption are deliberately kept rather than rolled back — resuming from the middle is cheaper
than restarting from zero, and the result is identical either way. An unreachable server is
reported as `Offline` rather than as a failure, since offline is this client's expected resting
state.

#### Concurrent runs
Three layers, because there are three distinct races:
- **Within a process** — sync is triggered from startup, a timer and a manual refresh button. A
  `SemaphoreSlim(1,1)` gate admits one run; overlapping callers are *turned away, not queued*,
  because a second immediate sync has nothing new to fetch. Two concurrent runs would share the
  single cursor and race on the same rows.
- **Across processes** — the WPF client claims a machine-wide named `Mutex` in `App.OnStartup`
  before the database, log file or telemetry reader is opened. A second launch signals the running
  instance (a named `EventWaitHandle`) to bring its window to the foreground and then exits. This
  matters beyond sync: the local SQLite store and the RaceRoom shared-memory block are both
  single-writer resources, and two instances would record duplicate laps. An `AbandonedMutexException`
  is treated as a successful claim, so a prior crash never locks the user out.
- **Against the server** — page reads are `AsNoTracking` and ordered by version, so a write landing
  mid-page cannot corrupt a page; it simply arrives on the next one.

### Telemetry Recording Pipeline
1. **Poll loop** — background task reads from RaceRoom shared memory at ~60 Hz
2. **Producer** — writes `TelemetryFrame` to a bounded `Channel<T>` (drops oldest on overflow)
3. **Consumer** — feeds frames into `SessionStateMachine`, persists events to SQLite
4. **Uploader** — background task drains unsent rows and POSTs them to the server API with retry

### State Machine (Part 3)
Handles the four required scenarios:
- **Session restart mid-run** — monotonic-time guard detects simulation time reset
- **Invalid lap** — validity latched for the lap, persisted even if lap time stays `-1`
- **Pit stop** — in-lap closes stint, out-lap opens next stint
- **Quit to menu** — partial lap discarded, session closed with `reason: Abandoned`

### Part 5: Input Telemetry Storage
- One row per lap, not one row per sample (~5,400 rows saved per 90-second lap at 60 Hz)
- Throttle/brake/steering buffered as `float[]` arrays during the lap
- On completion, each channel is **quantised to 16 bits, delta-encoded, then Brotli-compressed** into a single columnar blob
- A ~500-sample-per-channel preview array (min/max decimated, uncompressed `float32`) is stored alongside for fast plotting
- Trade-off: blobs are opaque to SQL (no WHERE clause filtering), but plotting is cheap (one primary-key fetch)

**Measured, not assumed.** The original design stored raw `float32` columns and relied on Brotli
alone. A verification probe disproved it: for a 90-second lap (64,800 bytes raw), `float32` +
Brotli produced **48,126 bytes — only 25.7% saved**, because float mantissa noise is
incompressible. Quantising to 16 bits and delta-encoding first produced **11,990 bytes (18.5% of
raw)** with a maximum round-trip error of **1.53e-5**, far below the resolution of any pedal or
wheel input. Columnar ordering alone is not enough; the data must be made compressible first.

See `Docs/CHOICES AND REASONS.md` for full write/read/storage cost analysis.

### Part 5: Input Telemetry Plotting
- The chart binds the **preview by default**, so opening a lap costs one primary-key fetch and no decompression
- Full resolution is **opt-in**: only then is the blob inflated and dequantised
- Traces are decimated to ~1,200 plotted points, since an SVG chart cannot resolve more points than it has pixels
- Previews are plotted against **lap-progress percentage**, not seconds. A preview is min/max decimated, so its samples are *not* evenly spaced in time; plotting it against a clock would place inputs at the wrong point in the lap
- Two-lap overlay reuses the same per-channel colours at lighter stroke weight, so the channel stays identifiable and the lap is distinguished by weight

---

## What We Didn't Build

### Deliberately Omitted (with justification)
- **Refresh token flow** — deferred to Docs/TODO.md
- **Comprehensive test coverage** — the brief states "tests are not required and we're not scoring coverage"
- **Visual polish** — the brief says "we are not scoring visual polish beyond stock Radzen"
- **CI/CD or containerization** — out of scope per the brief

### Sync Edge Cases Handled
- ✅ **Change cursor** — server-issued `ServerVersion` from a SQL `SEQUENCE`, shared across Games/Cars/Tracks and trimmed to the lowest truncated page
- ✅ **Wrong client clock** — the client never contributes to the version calculation; no timestamp is used as a cursor
- ✅ **Deletions on the server** — soft-deleted and versioned, propagated as tombstones, purged locally
- ✅ **Interrupted sync** — per-page transactions with the cursor committed alongside the rows, so a killed run resumes at the last fully-applied page
- ✅ **Concurrent runs** — in-process `SemaphoreSlim` gate, machine-wide `Mutex` single-instance guard on the WPF client, non-tracking ordered server reads

### Sync Edge Cases NOT Handled
- ❌ Schema migrations mid-sync (would require a versioned sync protocol)
- ❌ Conflicting writes from multiple clients (last-write-wins, no conflict resolution)
- ❌ Byzantine client clocks (e.g., negative timestamps) — assumes well-behaved clients
- ❌ Tombstone compaction — deleted rows are retained indefinitely so that arbitrarily stale clients converge; a production system would expire them past a retention window and force a full resync for clients older than it
- ❌ Cross-machine instance locking — the single-instance guard is per machine, so the same user on two PCs can run two clients

---

## Known Weaknesses

no refresh token flow yet (see Docs/TODO.md)
2. **DPAPI tokens don't survive machine migration** — re-login required if the app is copied to a different user profile or machine
3. **Input telemetry blobs are opaque to SQL** — queries like "laps where throttle exceeded 95%" require application-code decoding
4. **No server-side telemetry replay** — `GameTracker.Telemetry` targets `net10.0-windows`, so the server can't reference it (would need a split into pure + interop)
5. **Version gate is strict** — if RaceRoom updates the struct, the app refuses to start rather than attempting a best-effort parse
6. **Input quantisation is lossy** — inputs are stored to ~1.5e-5 precision. This is far below what a pedal or wheel can resolve, so it is invisible in practice, but the stored trace is not bit-identical to what was captured
7. **Two-lap overlay aligns on lap progress, not distance** — laps of different durations are compared by percentage completed. If one lap includes an off-track excursion, the same percentage is not the same corner. Aligning on distance travelled would be more correct but is not implemented
8. **Telemetry has only been validated against a single RaceRoom version** — the 3/5 layout was confirmed by a live spike on one machine; other builds are unverified

These are deliberate trade-offs or stretch goals beyond the brief's scope. See `Docs/CHOICES AND REASONS.md` for details.

---

## Testing the Telemetry Recording

1. Launch RaceRoom Racing Experience
2. Pick any free car and track (e.g., Aquila Alpin Skyline with the free Audi R8 LMS Ultra)
3. Enter a **Practice** session (not menus — the `$R3E` shared memory region is only created in-session)
4. Drive a few laps, make a pit stop, and quit to the menu
5. Check the WPF client UI — your session, stints, and laps should appear in the "Recorded Sessions" view
6. Select a lap flagged `Inputs` — the driver-input trace renders from the stored preview
7. Tick **Full resolution** to inflate the compressed blob, and use **Overlay** on a second lap to compare the two
8. Check the server database — telemetry rows should appear in `TelemetryRecords` after the uploader runs

### Scenarios to Test
- **Restart the session mid-run** — the app should open a new session, not corrupt the existing one
- **Cut a corner (invalid lap)** — the lap should appear in the UI flagged as invalid, not dropped
- **Make a pit stop** — in-lap closes one stint, out-lap opens the next
- **Quit to menu mid-lap** — the partial lap is discarded, the session closes cleanly

---

## Troubleshooting

### "The shared memory region '$R3E' was not found"
- RaceRoom is not running, or you're sitting in menus (not in a session)
- Some launcher configurations gate shared memory behind a setting — check RaceRoom's options

### "Version mismatch: expected 3/5, got X/Y"
- RaceRoom updated its shared memory layout
- Re-copy `R3E.cs` from `https://github.com/kwstudios-sweden/r3e-api` and rebuild
- Or widen the version gate to accept the new version after verifying the struct

### The client won't sync
- Check the JWT token in `%LOCALAPPDATA%\GameTracker\token.dat` — it may have expired (24-hour TTL)
- Check the server API is reachable (default: `https://localhost:7157`)
- Check the server logs for authentication failures
- The client log (`%LOCALAPPDATA%\GameTracker\logs\`) reports
  `Telemetry upload is paused because the session is not signed in` when the token has lapsed.
  Recorded laps stay queued locally and upload once you sign in again — nothing is lost.

### The server exits at startup with "Jwt:Key is not configured"
- The signing key has not been set on this machine; see step 4 of the setup instructions
- In production the key comes from the environment or a key vault as `Jwt__Key`, not user-secrets

### Every client was logged out after a server restart or deployment
- The `Jwt:Key` in use changed, which invalidates every previously issued token
- Confirm the key is stored durably and is identical across all server instances

---

## References

- **RaceRoom Shared Memory Layout:** `https://github.com/kwstudios-sweden/r3e-api`
- **RaceRoom Car/Track IDs:** `https://github.com/kwstudios-sweden/r3e-spectator-overlay` (lookup JSON)
- **Radzen Blazor Components:** `https://blazor.radzen.com/`

---

## License

MIT — see `LICENSE.txt`.

---

and `Docs/TODO.md` for deferred work._
