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
- **Compressed input telemetry storage** using Brotli-compressed columnar blobs

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

### 3. Set up the server database
```powershell
cd GameTrackerBlazorServerApp
dotnet ef database update
cd ..
```

This will:
- Create the SQL Server database (connection string in `appsettings.json`)
- Run migrations (schema + audit triggers)
- Seed the catalogue from `Data/r3e-data.json`
- Create `Admin` and `User` roles
- Create a default admin account (see seeder for credentials)

### 4. Set up the client database
The WPF client database is created automatically on first launch under:
```
%LOCALAPPDATA%\GameTracker\gametracker.db
```

No manual migration step is required.

### 5. Run the Blazor Server app
```powershell
cd GameTrackerBlazorServerApp
dotnet run
```

The app will start on `https://localhost:5001` (or as configured). Navigate there and log in with the seeded admin account.

### 6. Run the WPF Client app
```powershell
cd GameTrackerWpfClientApp
dotnet run
```

On first launch:
1. Log in using credentials from the server
2. The client will sync the catalogue (Games/Cars/Tracks) into local SQLite
3. The client is now usable offline

### 7. Record telemetry (optional)
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
	├── CHOICES AND REASONS.md           # Architecture decisions and justifications
	└── TODO.md                          # Deferred work
```

---

## User Roles

- **Admin** — full CRUD on Games, Cars, Tracks
- **User** — read-only access to catalogue

The seeder creates a default admin account. See `ApplicationDbContextSeeder.cs` for credentials.

---

## API Endpoints

### Authentication
- `POST /api/auth/login` — returns a 24-hour JWT (body: `{ "username": "...", "password": "..." }`)
- `POST /register` — Identity default (via `MapIdentityApi`)
- `POST /logout` — Identity default

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
- One row per lap, not one row per sample (~90,000 rows saved per lap)
- Throttle/brake/steering buffered as `float[]` arrays during the lap
- On completion, serialized **columnar**, Brotli-compressed to a single blob
- A ~500-sample preview array (min/max decimated) stored alongside for fast plotting
- Trade-off: blobs are opaque to SQL (no WHERE clause filtering), but plotting is cheap (one primary-key fetch)

See `Docs/CHOICES AND REASONS.md` for full write/read/storage cost analysis.

---

## What We Didn't Build

### Deliberately Omitted (with justification)
- **Refresh token flow** — deferred to TODO.md; 24-hour JWTs are acceptable for a test
- **Comprehensive test coverage** — the brief states "tests are not required and we're not scoring coverage"
- **Visual polish** — the brief says "we are not scoring visual polish beyond stock Radzen"
- **CI/CD or containerization** — out of scope per the brief

### Sync Edge Cases Handled
- ✅ Interrupted sync (resumable via continuation token)
- ✅ Deletions on server (tombstones propagated)
- ✅ Wrong client clock (client never contributes to version calculation)

### Sync Edge Cases NOT Handled
- ❌ Schema migrations mid-sync (would require a versioned sync protocol)
- ❌ Conflicting writes from multiple clients (last-write-wins, no conflict resolution)
- ❌ Byzantine client clocks (e.g., negative timestamps) — assumes well-behaved clients

---

## Known Weaknesses

1. **JWT expiry = forced re-login** — no refresh token flow yet (see TODO.md)
2. **DPAPI tokens don't survive machine migration** — re-login required if the app is copied to a different user profile or machine
3. **Input telemetry blobs are opaque to SQL** — queries like "laps where throttle exceeded 95%" require application-code decoding
4. **No server-side telemetry replay** — `GameTracker.Telemetry` targets `net10.0-windows`, so the server can't reference it (would need a split into pure + interop)
5. **Version gate is strict** — if RaceRoom updates the struct, the app refuses to start rather than attempting a best-effort parse

These are deliberate trade-offs or stretch goals beyond the brief's scope. See `Docs/CHOICES AND REASONS.md` for details.

---

## Testing the Telemetry Recording

1. Launch RaceRoom Racing Experience
2. Pick any free car and track (e.g., Aquila Alpin Skyline with the free Audi R8 LMS Ultra)
3. Enter a **Practice** session (not menus — the `$R3E` shared memory region is only created in-session)
4. Drive a few laps, make a pit stop, and quit to the menu
5. Check the WPF client UI — your session, stints, and laps should appear in the "Recorded Sessions" view
6. Check the server database — telemetry rows should appear in `TelemetryRecords` after the uploader runs

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
- Check the server API is reachable (default: `https://localhost:5001`)
- Check the server logs for authentication failures

---

## References

- **RaceRoom Shared Memory Layout:** `https://github.com/kwstudios-sweden/r3e-api`
- **RaceRoom Car/Track IDs:** `https://github.com/kwstudios-sweden/r3e-spectator-overlay` (lookup JSON)
- **Radzen Blazor Components:** `https://blazor.radzen.com/`

---

## License

_[TO BE SPECIFIED]_

---

_Last updated: [TO BE FILLED AT END OF IMPLEMENTATION]_
