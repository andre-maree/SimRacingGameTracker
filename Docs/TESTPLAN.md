# Test Plan — GameTracker

A step-by-step verification of the solution from a **clean clone** on a machine that is not the
development machine. Every value below was read from source, not assumed:

| Fact | Source |
|---|---|
| Server URL `https://localhost:7157` | `GameTrackerBlazorServerApp/Properties/launchSettings.json` |
| Client points at `https://localhost:7157/` | `GameTrackerWpfClientApp/App.xaml.cs` (`Server:BaseAddress`) |
| Admin email defaults to `admin@gametracker.local` | `Data/DbSeeder.cs` line 54 |
| Admin is **only** created if `Seed:AdminPassword` is set | `Data/DbSeeder.cs` lines 71–75 |
| Login body uses `email` (not `username`) | `GameTracker.Domain/Dtos/AuthDtos.cs` |
| Server DB is LocalDB | `appsettings.json` → `DefaultConnection` |
| Client DB is SQLite at `%LOCALAPPDATA%\GameTracker\` | `App.xaml.cs` |

---

## Division of labour

**What I (the agent) already did — reading, not running:**
- Verified every instruction in this plan against the source files listed above.
- Confirmed the solution builds with 0 warnings / 0 errors.
- Confirmed which surfaces need a JWT (sync + telemetry upload) and which are local-only
  (Sessions, Cars, Tracks).

**What only you can do — and why it matters:**
- Run against a real LocalDB instance. **This is the genuine gap.** LocalDB presence,
  first-run EF migration, and HTTPS dev-cert trust all behave differently on a machine that
  isn't the one this was written on. I cannot validate them by reading code.
- Drive RaceRoom for the live telemetry capture (Part 5). Nothing in the recording path has
  been exercised against real shared-memory data — only against its own unit tests.
- Confirm the DPAPI token actually round-trips across an app restart.

> Sections marked **[CRITICAL]** must all pass for the solution to be considered working.
> Phases 3, 4 and 8d are the ones most likely to expose a real defect: first-run database
> creation, the auth path, and offline telemetry queueing.

---

## Phase 0 — Prerequisites **[CRITICAL]**

1. Confirm the .NET 10 SDK is present:
   ```powershell
   dotnet --list-sdks
   ```
   Expect a `10.x` entry.

2. Confirm SQL Server LocalDB exists:
   ```powershell
   sqllocaldb info
   ```
   **Expected:** a list containing `MSSQLLocalDB`.

   **If this command is not recognised, STOP.** The server cannot start. LocalDB ships with
   the *SQL Server Express LocalDB* installer or the Visual Studio "Data storage and
   processing" workload. This is the single most likely clean-machine failure.

3. Start the LocalDB instance if it is stopped:
   ```powershell
   sqllocaldb start MSSQLLocalDB
   ```

4. Trust the ASP.NET Core HTTPS development certificate:
   ```powershell
   dotnet dev-certs https --trust
   ```
   Accept the Windows prompt. **If you skip this, the WPF client's HTTPS calls to the server
   will fail** even though the server itself looks healthy in a browser.

---

## Phase 1 — Clean clone and build **[CRITICAL]**

1. Clone into a directory that is **not** your existing working copy:
   ```powershell
   cd $env:TEMP
   git clone https://github.com/andre-maree/SimRacingGameTracker.git gametracker-test
   cd gametracker-test
   ```

2. Build the whole solution:
   ```powershell
   dotnet build GameTrackerSolution.slnx
   ```

   **Expected:** `Build succeeded. 0 Warning(s) 0 Error(s)`

   **Record the actual result here:** _______________

---

## Phase 2 — Configure the signing key and admin password **[CRITICAL]**

The server **refuses to start** without `Jwt:Key`, so set it first:

```powershell
cd GameTrackerBlazorServerApp
$bytes = New-Object byte[] 64
[System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
dotnet user-secrets set "Jwt:Key" ([Convert]::ToBase64String($bytes))
```

Startup also fails if the key is shorter than 32 bytes. Keep the value stable: replacing it
invalidates every token already issued to a client.

No credential is committed to the repository. The seeder **silently skips** admin creation when
no password is configured — so if you skip this step, the app will start normally and you will
only discover the problem when login fails with a 401.

```powershell
dotnet user-secrets set "Seed:AdminPassword" "Test123!Pass"
```

The password must satisfy the ASP.NET Identity default policy (8+ chars, upper, lower, digit,
non-alphanumeric). The example above does.

Optionally override the email (otherwise `admin@gametracker.local` is used):
```powershell
dotnet user-secrets set "Seed:AdminEmail" "admin@gametracker.local"
cd ..
```

---

## Phase 3 — First server run: migration and seeding **[CRITICAL]**

This is the step that behaves differently on a fresh machine. The database does not exist yet;
it is created at startup.

1. Start the server:
   ```powershell
   dotnet run --project GameTrackerBlazorServerApp --launch-profile https
   ```

2. **Watch the console output.** You are looking for:
   - EF Core applying migrations (creating the `aspnet-GameTrackerBlazorServerApp-...` database)
   - No unhandled exception
   - `Now listening on: https://localhost:7157`

   | Symptom | Cause | Fix |
   |---|---|---|
   | Hangs ~30s then a connection/network error | LocalDB not running | `sqllocaldb start MSSQLLocalDB` |
   | `login failed for user` | LocalDB instance owned by a different user profile | `sqllocaldb delete MSSQLLocalDB` then `sqllocaldb create MSSQLLocalDB` |
   | Starts but login later 401s | `Seed:AdminPassword` not set before **first** run | See "Re-seeding" below |
   | Exits immediately with `Jwt:Key is not configured` | Signing key secret not set | Repeat Phase 2 |

3. Confirm the database was created:
   ```powershell
   sqlcmd -S "(localdb)\mssqllocaldb" -Q "SELECT name FROM sys.databases"
   ```
   **Expected:** a database whose name starts with `aspnet-GameTrackerBlazorServerApp-`.

4. Browse to `https://localhost:7157` — the site should load with no certificate warning.

### Re-seeding if you set the password too late
The seeder returns early when the admin user already exists, so simply setting the secret and
restarting will **not** retro-fit a password. Drop the database and start over:
```powershell
sqlcmd -S "(localdb)\mssqllocaldb" -Q "DROP DATABASE [aspnet-GameTrackerBlazorServerApp-499bb887-6a1c-4193-b234-0d7d2dbd4182]"
```
Then repeat Phase 2 and Phase 3.

---

## Phase 4 — Prove the auth path via the API **[CRITICAL]**

This isolates the server half from all UI concerns. Leave the server running; use a **second**
PowerShell window.

```powershell
$body = @{ email = "admin@gametracker.local"; password = "Test123!Pass" } | ConvertTo-Json

$login = Invoke-RestMethod -Uri "https://localhost:7157/api/auth/login" `
	-Method Post -Body $body -ContentType "application/json"

$login.accessToken.Substring(0,20)   # sanity: a JWT prefix
$login.roles                          # expect: Admin
```

**Expected:** a long token string and a `roles` collection containing `Admin`.

Now replay the token against a protected endpoint:
```powershell
Invoke-RestMethod -Uri "https://localhost:7157/api/sync/changes?since=0&take=5" `
	-Headers @{ Authorization = "Bearer $($login.accessToken)" }
```
**Expected:** JSON containing catalogue rows and a cursor/version value.

Negative test — the same call with no header:
```powershell
Invoke-RestMethod -Uri "https://localhost:7157/api/sync/changes?since=0&take=5"
```
**Expected:** `401 Unauthorized`.

| Result | Meaning |
|---|---|
| Token returned, sync returns rows | Server auth + seeding are correct. Any later failure is client-side. |
| `401` on login | Admin was never seeded — see "Re-seeding" above. |
| `423` on login | Identity lockout from repeated failures. Wait, or drop the DB. |
| Sync returns `[]` / zero rows | `Data/r3e-data.json` missing; catalogue seeding was skipped. |

---

## Phase 5 — Server admin UI

1. Sign in at `https://localhost:7157/Account/Login` with the same credentials.
2. Navigate to the Cars and Tracks admin pages.
3. **Create** a car with a recognisable name (e.g. `ZZ-TEST-CAR`).
4. **Edit** it, then confirm the change persists after a page refresh.
5. Leave `ZZ-TEST-CAR` in place — Phase 6 uses it to prove sync moved real data.

---

## Phase 6 — WPF client: first run, login, sync **[CRITICAL]**

Keep the server running.

1. Delete any previous client state so this is genuinely a first run:
   ```powershell
   Remove-Item "$env:LOCALAPPDATA\GameTracker" -Recurse -Force -ErrorAction SilentlyContinue
   ```

2. Start the client:
   ```powershell
   dotnet run --project GameTrackerWpfClientApp
   ```

3. **First-run expectations:**
   - The window opens; the toolbar shows **Offline** and a **Sign in** button.
   - Cars and Tracks pages are reachable but **empty** — correct, the local mirror has never
	 been synced.
   - Confirm the SQLite file was created:
	 ```powershell
	 Test-Path "$env:LOCALAPPDATA\GameTracker\gametracker.db"
	 ```
	 **Expected:** `True`

4. Click **Sync catalogue** *while still signed out*.
   **Expected:** you are redirected to the login page rather than shown a silent failure.

5. On the login page enter the admin email and password and submit.
   **Expected:** you land back on Home; the toolbar now shows an **Admin** badge and a
   **Sign out** button.

6. Click **Sync catalogue**.
   **Expected:** a success notification reporting a non-zero row count.

7. Open **Cars** and search for `ZZ-TEST-CAR`.
   **Expected:** present — this proves data actually travelled server → client.

8. Run **Sync catalogue** a second time.
   **Expected:** success with **0 rows** applied. This proves the incremental cursor works and
   the client is not re-downloading the whole catalogue every time.

---

## Phase 7 — Token persistence across restart **[CRITICAL]**

1. Confirm the token file exists:
   ```powershell
   Test-Path "$env:LOCALAPPDATA\GameTracker\token.dat"
   ```
   **Expected:** `True`

2. Confirm it is encrypted, not plaintext:
   ```powershell
   Get-Content "$env:LOCALAPPDATA\GameTracker\token.dat" -Raw | Select-Object -First 1
   ```
   **Expected:** binary/garbage. **If you can read a JWT here, that is a security defect** —
   DPAPI protection is not being applied.

3. Close the client completely and restart it.
   **Expected:** the toolbar shows **Sign out** immediately — no login required. This proves
   `AuthenticationState.InitialiseAsync()` restored the DPAPI-protected token.

4. Click **Sign out**.
   **Expected:** toolbar returns to **Offline** / **Sign in**, and `token.dat` is cleared or
   removed.

---

## Phase 8 — Telemetry capture and upload **[CRITICAL]**

Verified constants, read from source:

| Value | Source |
|---|---|
| Poll rate 60 Hz | `SessionRecorder.cs` line 50 (`SampleRateHz`) |
| Idle upload poll: **30 s** | `TelemetryUploadService.cs` line 40 (`IdleInterval`) |
| Upload batch size: 500 rows | `TelemetryUploadService.cs` line 37 |
| Retry backoff: 5 s → max 5 min | `TelemetryUploadService.cs` lines 43, 50 |

> **Ordering trap — do Phase 6 sync BEFORE driving.**
> `ResolveGameIdAsync` (`SessionRecorder.cs` lines 402–407) looks up the RaceRoom game row by
> `ShortName == "R3E"` from the **synced** catalogue, and falls back to `0` if the catalogue has
> never synced. Laps recorded before your first sync are still saved — by design — but will
> carry `GameId = 0` locally. Sync first and this never arises.

### 8a — Capture

1. Ensure the client is running, signed in, and Phase 6's sync has completed.
2. Launch RaceRoom Racing Experience and enter a session.
3. Watch the client's status area as the car goes on track.
   **Expected:** the status updates to show it is connected/recording. It refreshes at most
   twice a second (`NotificationInterval` = 500 ms), so it should read smoothly, not flicker.
4. Drive **at least three complete flying laps**. Phase 9's overlay needs two; a third gives
   margin if one is invalidated by a cut or an off.
5. Return to the pits and exit to the menu.
   **Expected:** the session closes cleanly — no error dialog, no hang.

### 8b — Local persistence (before any upload)

Open **Sessions** in the client.

**Expected:**
- The session is listed with the **correct car and track names** — this proves the catalogue
  mirror resolved server-issued ids, not placeholders.
- Lap count matches what you actually drove.
- Lap times are plausible and match what RaceRoom displayed.

If car/track show as blank or numeric ids, the catalogue sync did not apply — return to Phase 6.

### 8c — Upload

1. Leave the client **running and signed in**. The uploader only drains when
   `IsAuthenticated` is true, and it polls every **30 seconds** when idle — so wait at least
   that long, and up to a minute, before concluding anything failed.
2. Confirm server-side. **Note the schema:** the server stores only the session header plus
   per-lap `TelemetryRecords`. There is deliberately **no `Laps` or `Stints` table** —
   `ApplicationDbContext.cs` line 99 calls `entity.Ignore(e => e.Stints)`, because stints and
   laps live only in the client SQLite store. One `TelemetryRecords` row corresponds to one lap.
   ```powershell
   sqlcmd -S "(localdb)\mssqllocaldb" `
	 -d "aspnet-GameTrackerBlazorServerApp-499bb887-6a1c-4193-b234-0d7d2dbd4182" `
	 -Q "SELECT COUNT(*) AS Sessions FROM Sessions; SELECT COUNT(*) AS LapRecords FROM TelemetryRecords"
   ```
   **Expected:** `Sessions` ≥ 1, and `LapRecords` equal to the number of laps you drove.

   To see the detail rather than just counts:
   ```powershell
   sqlcmd -S "(localdb)\mssqllocaldb" `
	 -d "aspnet-GameTrackerBlazorServerApp-499bb887-6a1c-4193-b234-0d7d2dbd4182" `
	 -Q "SELECT LapNumber, LapTime, IsValid FROM TelemetryRecords ORDER BY LapNumber"
   ```
   **Expected:** lap times matching what RaceRoom showed you.

3. Re-run the same query after another minute.
   **Expected:** counts are **unchanged**. Rows are stamped as uploaded and must not be
   re-sent — a climbing count would mean duplicate uploads.

### 8d — Offline queue *(the test that actually proves the design)*

This is the single most valuable telemetry test: it demonstrates capture is independent of
connectivity.

1. **Stop the server** (Ctrl+C in the server window).
2. With the client still running, drive **one more lap** in RaceRoom.
   **Expected:** recording continues completely normally. **No error dialog, no stall, no
   dropped frames.** If driving is disrupted by the server being down, that is a genuine
   architectural failure worth reporting.
3. Check **Sessions** — the new lap appears locally despite there being no server.
4. Restart the server:
   ```powershell
   dotnet run --project GameTrackerBlazorServerApp --launch-profile https
   ```
5. Wait up to ~30 s (idle poll) — allow longer if the uploader had already backed off after
   failed attempts, since backoff grows from 5 s toward a 5-minute ceiling.
6. Re-run the Phase 8c count query.
   **Expected:** `LapRecords` has increased by the offline lap, **with no user action taken**.

### 8e — Token expiry behaviour *(optional, destructive)*

To confirm a 401 mid-upload is handled rather than looping: sign out while laps are pending,
then observe that uploads pause and resume after signing back in.


---

## Phase 9 — Input telemetry plot (Part 5)

1. In **Sessions**, select a session, then select a lap.
2. **Expected:** the input trace chart renders throttle/brake/steering traces.
3. Select a **second** lap to overlay.
   **Expected:** both traces render together for comparison.
documented in `Docs/TODO.md`
   so laps of differing length will not line up perfectly. Confirm this is what you observe
   rather than treating it as a new bug.

---

## Results summary

| Phase | Pass / Fail | Notes |
|---|---|---|
| 0 — Prerequisites | | |
| 1 — Clean build | | |
| 2 — Admin secret | | |
| 3 — Migration & seed | | |
| 4 — API auth proof | | |
| 5 — Server admin UI | | |
| 6 — Client login & sync | | |
| 7 — Token persistence | | |
| 8a — Capture | | |
| 8b — Local persistence | | |
| 8c — Upload | | |
| 8d — Offline queue | | |
| 9 — Input plot | | |

---

## Cleanup

```powershell
# Client state
Remove-Item "$env:LOCALAPPDATA\GameTracker" -Recurse -Force

# Server database
sqlcmd -S "(localdb)\mssqllocaldb" -Q "DROP DATABASE [aspnet-GameTrackerBlazorServerApp-499bb887-6a1c-4193-b234-0d7d2dbd4182]"

# Test clone
Remove-Item "$env:TEMP\gametracker-test" -Recurse -Force
```
